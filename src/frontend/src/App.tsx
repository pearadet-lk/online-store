import { useCallback, useEffect, useMemo, useState } from "react";

type Product = {
  productId: string;
  name: string;
  description: string;
  price: number;
  isActive?: boolean;
};

type CartItem = {
  productId: string;
  quantity: number;
  unitPrice: number;
};

const demoUserId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

function newIdempotencyKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `checkout-${Date.now()}`;
}

export default function App() {
  const [products, setProducts] = useState<Product[]>([]);
  const [cart, setCart] = useState<CartItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [checkoutResult, setCheckoutResult] = useState<unknown>(null);
  const [checkoutBusy, setCheckoutBusy] = useState(false);

  const loadProducts = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch("/api/products");
      if (!res.ok) {
        throw new Error(`Products failed: ${res.status}`);
      }
      const data: Product[] = await res.json();
      setProducts(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load products");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadProducts();
  }, [loadProducts]);

  const persistCart = useCallback(async (items: CartItem[]) => {
    const res = await fetch(`/api/carts/${demoUserId}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userId: demoUserId, items }),
    });
    if (!res.ok) {
      throw new Error(`Cart save failed: ${res.status}`);
    }
  }, []);

  const addToCart = useCallback(
    async (p: Product) => {
      setError(null);
      const next = [...cart];
      const existing = next.find((x) => x.productId === p.productId);
      if (existing) {
        existing.quantity += 1;
      } else {
        next.push({ productId: p.productId, quantity: 1, unitPrice: p.price });
      }
      try {
        await persistCart(next);
        setCart(next);
      } catch (e) {
        setError(e instanceof Error ? e.message : "Cart error");
      }
    },
    [cart, persistCart],
  );

  const checkout = useCallback(async () => {
    setCheckoutBusy(true);
    setCheckoutResult(null);
    setError(null);
    try {
      if (cart.length === 0) {
        throw new Error("Cart is empty. Add a product first.");
      }

      const res = await fetch("/api/checkout", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": newIdempotencyKey(),
        },
        body: JSON.stringify({
          userId: demoUserId,
          currency: "USD",
          items: cart.map((c) => ({
            productId: c.productId,
            quantity: c.quantity,
            unitPrice: c.unitPrice,
          })),
        }),
      });

      const body = await res.json().catch(() => ({}));
      if (!res.ok) {
        throw new Error(
          (body as { error?: string }).error ||
            `Checkout failed: ${res.status}`,
        );
      }

      setCheckoutResult(body);
      setCart([]);
      await loadProducts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Checkout error");
    } finally {
      setCheckoutBusy(false);
    }
  }, [cart, loadProducts]);

  const cartSummary = useMemo(
    () => cart.reduce((sum, c) => sum + c.quantity * c.unitPrice, 0),
    [cart],
  );

  return (
    <main>
      <h1>Online Store (React → HTTPS → Gateway)</h1>
      <p style={{ color: "#475569", marginTop: 0 }}>
        Demo user is pre-seeded in UserService. Cart is stored via CartService
        through the gateway so checkout validation matches the diagram.
      </p>

      {error ? <p className="error">{error}</p> : null}

      <section>
        <h2 style={{ marginTop: 0 }}>Catalog</h2>
        {loading ? (
          <p>Loading…</p>
        ) : (
          <ul>
            {products.map((p) => (
              <li key={p.productId}>
                <span>
                  <strong>{p.name}</strong> — ${p.price.toFixed(2)}
                </span>
                <button type="button" onClick={() => void addToCart(p)}>
                  Add to cart
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h2 style={{ marginTop: 0 }}>Cart</h2>
        {cart.length === 0 ? (
          <p>Empty</p>
        ) : (
          <ul>
            {cart.map((c) => (
              <li key={c.productId}>
                <span>{c.productId}</span>
                <span>
                  qty {c.quantity} @ ${c.unitPrice.toFixed(2)}
                </span>
              </li>
            ))}
          </ul>
        )}
        <p>
          <strong>Total:</strong> ${cartSummary.toFixed(2)}
        </p>
        <button type="button" disabled={checkoutBusy} onClick={() => void checkout()}>
          {checkoutBusy ? "Checking out…" : "Checkout"}
        </button>
      </section>

      {checkoutResult ? (
        <section>
          <h2 style={{ marginTop: 0 }}>Last checkout</h2>
          <pre>{JSON.stringify(checkoutResult, null, 2)}</pre>
        </section>
      ) : null}
    </main>
  );
}
