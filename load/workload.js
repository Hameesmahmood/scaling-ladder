// The ladder's workload, the same for every step. An open model: requests start on a fixed
// schedule (RATE per second) whether or not earlier ones have finished, so a slow server shows up
// as latency and dropped requests instead of quietly slowing the test down.
//
//   docker compose run --rm -e RATE=50 -e DURATION=60s k6 run --quiet /load/workload.js
//
// The mix: 90% look up a shipment (80% of those from the 10,000 in transit, 20% from all 1,000,000),
// 8% list a customer's shipments, 2% record a tracking event on a shipment in transit.
import http from "k6/http";
import { Counter } from "k6/metrics";

const TARGET = __ENV.TARGET || "http://api:8080";
const RATE = Number(__ENV.RATE || 10);
const DURATION = __ENV.DURATION || "60s";
const SHIPMENTS = 1_000_000;
const CUSTOMERS = 100_000;
const ENDPOINTS = ["get_shipment", "list_shipments", "post_event"];
// With CHECK_STALE=1, every write becomes read, write, read again, and the second read is checked
// for the event just recorded: the customer refreshing after the courier's scan. Off for the ladder
// itself, because it adds two reads per write and changes the mix.
const CHECK_STALE = __ENV.CHECK_STALE === "1";
const readsAfterWrite = new Counter("reads_after_write");
const staleReads = new Counter("stale_reads");

export const options = {
  discardResponseBodies: true,
  scenarios: {
    mix: {
      executor: "constant-arrival-rate",
      // Expressed per 100 s so fractional rates work: the baseline can't sustain even 1 a second.
      rate: Math.round(RATE * 100),
      timeUnit: "100s",
      duration: DURATION,
      preAllocatedVUs: Math.min(Math.max(Math.ceil(RATE), 10), 400),
      maxVUs: Math.min(Math.max(Math.ceil(RATE * 4), 50), 3000),
    },
  },
  summaryTrendStats: ["avg", "min", "med", "p(90)", "p(95)", "p(99)", "max"],
  // Always-passing thresholds, only so k6 reports latency for each endpoint separately.
  thresholds: Object.fromEntries(ENDPOINTS.map((n) => [`http_req_duration{name:${n}}`, ["max>=0"]])),
};

const trackingNumber = (id) => "SL" + String(id).padStart(10, "0");
const hot = () => 100 * (1 + Math.floor(Math.random() * (SHIPMENTS / 100)));
const any = () => 1 + Math.floor(Math.random() * SHIPMENTS);
const params = (name, extra = {}) => ({ tags: { name }, timeout: "10s", ...extra });

export default function () {
  const r = Math.random();
  if (r < 0.9) {
    const id = Math.random() < 0.8 ? hot() : any();
    http.get(`${TARGET}/v1/shipments/${trackingNumber(id)}`, params("get_shipment"));
  } else if (r < 0.98) {
    const customer = 1 + Math.floor(Math.random() * CUSTOMERS);
    http.get(`${TARGET}/v1/customers/${customer}/shipments?limit=20`, params("list_shipments"));
  } else if (!CHECK_STALE) {
    const body = JSON.stringify({ status: "in_transit", location: "Load test hub" });
    http.post(`${TARGET}/v1/shipments/${trackingNumber(hot())}/events`, body,
      params("post_event", { headers: { "Content-Type": "application/json" } }));
  } else {
    readAfterWrite(trackingNumber(hot()));
  }
}

function readAfterWrite(number) {
  const url = `${TARGET}/v1/shipments/${number}`;
  http.get(url, params("get_shipment"));
  // A location no other request uses, so the check can't be fooled by another write to this shipment.
  const location = `Check ${__VU}-${__ITER}`;
  const posted = http.post(`${url}/events`, JSON.stringify({ status: "in_transit", location }),
    params("post_event", { headers: { "Content-Type": "application/json" } }));
  if (posted.status !== 201) return;
  const read = http.get(url, params("get_shipment", { responseType: "text" }));
  if (read.status !== 200) return;
  readsAfterWrite.add(1);
  // Newest first, at most 20; a later write by someone else would push this one down, not out.
  if (!read.json("events").some((e) => e.location === location)) staleReads.add(1);
}

// One line of JSON on stdout, for scripts/ladder.py to read.
export function handleSummary(data) {
  const m = data.metrics;
  const ms = (v) => (v === undefined ? null : Math.round(v * 100) / 100);
  // An endpoint with no requests in the run (post_event is 2% of traffic) reports nulls, not zeros.
  const latency = (values = {}) => (!values.max ? null : {
    p50: ms(values.med), p90: ms(values["p(90)"]), p95: ms(values["p(95)"]), p99: ms(values["p(99)"]),
    avg: ms(values.avg), max: ms(values.max),
  });
  const result = {
    rate: RATE,
    duration: DURATION,
    requests: m.http_reqs?.values.count ?? 0,
    throughput: ms(m.http_reqs?.values.rate ?? 0),
    failedRate: m.http_req_failed?.values.rate ?? 0,
    dropped: m.dropped_iterations?.values.count ?? 0,
    latencyMs: latency(m.http_req_duration?.values) ?? { p50: null, p90: null, p95: null, p99: null, avg: null, max: null },
    readsAfterWrite: m.reads_after_write?.values.count ?? 0,
    staleReads: m.stale_reads?.values.count ?? 0,
    byEndpoint: Object.fromEntries(ENDPOINTS.map((n) => [n, latency(m[`http_req_duration{name:${n}}`]?.values)])),
  };
  return { stdout: JSON.stringify(result) + "\n" };
}
