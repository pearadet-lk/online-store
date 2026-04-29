import http from "k6/http";
import { check, sleep } from "k6";

const gatewayBaseUrl = __ENV.GATEWAY_URL || "http://localhost:5152";
const orderBaseUrl = __ENV.ORDER_URL || "http://localhost:18082";
const loginEmail = __ENV.LOGIN_EMAIL || "demo@example.com";
const loginPassword = __ENV.LOGIN_PASSWORD || "demo-password";
const currency = __ENV.CURRENCY || "USD";

export const options = {
  vus: Number(__ENV.VUS || 5),
  iterations: Number(__ENV.ITERATIONS || 30),
  thresholds: {
    http_req_failed: ["rate<0.05"],
    http_req_duration: ["p(95)<1500"],
    checks: ["rate>0.95"],
  },
  summaryTrendStats: ["avg", "min", "med", "max", "p90", "p95", "p99"],
};

function pickItem(iteration) {
  const even = iteration % 2 === 0;
  return {
    productId: even
      ? "11111111-1111-1111-1111-111111111111"
      : "22222222-2222-2222-2222-222222222222",
    quantity: (iteration % 3) + 1,
    unitPrice: even ? 39.99 : 59.99,
  };
}

function login() {
  const payload = JSON.stringify({
    email: loginEmail,
    password: loginPassword,
  });

  const res = http.post(`${gatewayBaseUrl}/api/users/login`, payload, {
    headers: { "Content-Type": "application/json" },
    tags: { endpoint: "login" },
  });

  check(res, {
    "login status is 200": (r) => r.status === 200,
    "login has access token": (r) => !!r.json("accessToken"),
    "login has refresh token": (r) => !!r.json("refreshToken"),
    "login has user id": (r) => !!r.json("user.userId"),
  });

  return {
    accessToken: res.json("accessToken"),
    userId: res.json("user.userId"),
  };
}

function createOrderDirect(userId, item) {
  const payload = JSON.stringify({
    userId,
    currency,
    items: [item],
  });

  const res = http.post(`${orderBaseUrl}/orders`, payload, {
    headers: { "Content-Type": "application/json" },
    tags: { endpoint: "orders_create_direct" },
  });

  check(res, {
    "direct order status is 201": (r) => r.status === 201,
    "direct order has id": (r) => !!r.json("orderId"),
  });

  return res.json("orderId");
}

function checkoutViaGateway(accessToken, userId, item, iter) {
  const payload = JSON.stringify({
    userId,
    currency,
    items: [item],
  });

  const idempotencyKey = `k6-checkout-${__VU}-${iter}-${Date.now()}`;
  const res = http.post(`${gatewayBaseUrl}/api/checkout`, payload, {
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${accessToken}`,
      "Idempotency-Key": idempotencyKey,
    },
    tags: { endpoint: "checkout_gateway" },
  });

  check(res, {
    "checkout status is 200": (r) => r.status === 200,
    "checkout has order id": (r) => !!r.json("order.orderId"),
  });
}

export default function () {
  const iter = __ITER + 1;
  const item = pickItem(iter);
  const auth = login();

  if (!auth.accessToken || !auth.userId) {
    return;
  }

  createOrderDirect(auth.userId, item);
  checkoutViaGateway(auth.accessToken, auth.userId, item, iter);
  sleep(0.2);
}
