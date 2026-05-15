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

type UserProfile = {
  userId: string;
  email: string;
  fullName: string;
  createdAt: string;
};

type LoginResponse = {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  user: UserProfile;
};

const authStorageKey = "online-store-auth-react";

function newIdempotencyKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `checkout-${Date.now()}`;
}

function newProductId(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `product-${Date.now()}`;
}

export default function App() {
  const [loginEmail, setLoginEmail] = useState("demo@example.com");
  const [loginPassword, setLoginPassword] = useState("demo-password");
  const [registerEmail, setRegisterEmail] = useState("");
  const [registerPassword, setRegisterPassword] = useState("");
  const [registerFullName, setRegisterFullName] = useState("");
  const [authToken, setAuthToken] = useState<string | null>(null);
  const [refreshToken, setRefreshToken] = useState<string | null>(null);
  const [currentUser, setCurrentUser] = useState<UserProfile | null>(null);
  const [loginBusy, setLoginBusy] = useState(false);
  const [registerBusy, setRegisterBusy] = useState(false);
  const [loginMessage, setLoginMessage] = useState<string | null>(null);
  const [registerMessage, setRegisterMessage] = useState<string | null>(null);
  const [products, setProducts] = useState<Product[]>([]);
  const [cart, setCart] = useState<CartItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [checkoutResult, setCheckoutResult] = useState<unknown>(null);
  const [checkoutBusy, setCheckoutBusy] = useState(false);
  const [isAdminView, setIsAdminView] = useState(false);
  const [adminProducts, setAdminProducts] = useState<Product[]>([]);
  const [adminLoading, setAdminLoading] = useState(false);
  const [newProductName, setNewProductName] = useState("");
  const [newProductDescription, setNewProductDescription] = useState("");
  const [newProductPrice, setNewProductPrice] = useState("0");
  const [adminMessage, setAdminMessage] = useState<string | null>(null);
  const [createBusy, setCreateBusy] = useState(false);

  useEffect(() => {
    try {
      const raw = localStorage.getItem(authStorageKey);
      if (!raw) {
        return;
      }
      const parsed = JSON.parse(raw) as { accessToken?: string; refreshToken?: string; user?: UserProfile };
      if (parsed.accessToken && parsed.refreshToken && parsed.user?.userId) {
        setAuthToken(parsed.accessToken);
        setRefreshToken(parsed.refreshToken);
        setCurrentUser(parsed.user);
        setLoginEmail(parsed.user.email);
      }
    } catch {
      localStorage.removeItem(authStorageKey);
    }
  }, []);

  const persistAuth = useCallback((token: string, nextRefreshToken: string, user: UserProfile) => {
    localStorage.setItem(authStorageKey, JSON.stringify({ accessToken: token, refreshToken: nextRefreshToken, user }));
  }, []);

  const logout = useCallback(() => {
    if (refreshToken) {
      void fetch("/api/users/logout", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken }),
      });
    }
    setAuthToken(null);
    setRefreshToken(null);
    setCurrentUser(null);
    setCart([]);
    setLoginMessage("Logged out.");
    localStorage.removeItem(authStorageKey);
  }, [refreshToken]);

  const tryRefreshToken = useCallback(async (): Promise<string | null> => {
    if (!refreshToken) {
      return null;
    }

    const res = await fetch("/api/users/refresh", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
    });
    const body = (await res.json().catch(() => ({}))) as Partial<LoginResponse>;
    if (!res.ok || !body.accessToken || !body.refreshToken || !body.user?.userId) {
      return null;
    }

    setAuthToken(body.accessToken);
    setRefreshToken(body.refreshToken);
    setCurrentUser(body.user);
    persistAuth(body.accessToken, body.refreshToken, body.user);
    return body.accessToken;
  }, [persistAuth, refreshToken]);

  const authenticatedFetch = useCallback(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const headers = new Headers(init?.headers ?? {});
    if (authToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${authToken}`);
    }

    let response = await fetch(input, { ...init, headers });
    if (response.status !== 401) {
      return response;
    }

    const refreshedToken = await tryRefreshToken();
    if (!refreshedToken) {
      return response;
    }

    const retryHeaders = new Headers(init?.headers ?? {});
    if (!retryHeaders.has("Authorization")) {
      retryHeaders.set("Authorization", `Bearer ${refreshedToken}`);
    }
    response = await fetch(input, { ...init, headers: retryHeaders });
    return response;
  }, [authToken, tryRefreshToken]);

  const login = useCallback(async () => {
    setLoginBusy(true);
    setLoginMessage(null);
    setError(null);
    try {
      const res = await fetch("/api/users/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email: loginEmail.trim(), password: loginPassword }),
      });

      const body = (await res.json().catch(() => ({}))) as Partial<LoginResponse> & { error?: string };
      if (!res.ok || !body.accessToken || !body.user?.userId) {
        throw new Error(body.error || "Invalid login credentials.");
      }

      setAuthToken(body.accessToken);
      setRefreshToken(body.refreshToken ?? null);
      setCurrentUser(body.user);
      if (body.refreshToken) {
        persistAuth(body.accessToken, body.refreshToken, body.user);
      }
      setLoginMessage(`Logged in as ${body.user.fullName}`);
      setCart([]);
    } catch (e) {
      setLoginMessage(e instanceof Error ? e.message : "Login failed");
    } finally {
      setLoginBusy(false);
    }
  }, [loginEmail, loginPassword, persistAuth]);

  const register = useCallback(async () => {
    setRegisterBusy(true);
    setRegisterMessage(null);
    try {
      const res = await fetch("/api/users/register", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          email: registerEmail.trim(),
          password: registerPassword,
          fullName: registerFullName.trim(),
        }),
      });

      const body = (await res.json().catch(() => ({}))) as { userId?: string; error?: string };
      if (!res.ok || !body.userId) {
        throw new Error(body.error || "Registration failed.");
      }

      setRegisterMessage("Registration successful. You can now login.");
      setLoginEmail(registerEmail.trim());
      setLoginPassword(registerPassword);
      setRegisterEmail("");
      setRegisterPassword("");
      setRegisterFullName("");
    } catch (e) {
      setRegisterMessage(e instanceof Error ? e.message : "Registration failed");
    } finally {
      setRegisterBusy(false);
    }
  }, [registerEmail, registerFullName, registerPassword]);

  const loadProducts = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await authenticatedFetch("/api/products");
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
  }, [authenticatedFetch]);

  const loadAdminProducts = useCallback(async () => {
    setAdminLoading(true);
    setError(null);
    try {
      const res = await authenticatedFetch("/api/admin/products");
      if (!res.ok) {
        throw new Error(`Admin products failed: ${res.status}`);
      }
      const data: Product[] = await res.json();
      setAdminProducts(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load admin products");
    } finally {
      setAdminLoading(false);
    }
  }, [authenticatedFetch]);

  useEffect(() => {
    void loadProducts();
  }, [loadProducts]);

  useEffect(() => {
    if (!isAdminView) {
      return;
    }
    void loadAdminProducts();
  }, [isAdminView, loadAdminProducts]);

  const persistCart = useCallback(async (items: CartItem[]) => {
    if (!currentUser?.userId) {
      throw new Error("Please login first.");
    }
    const res = await authenticatedFetch(`/api/carts/${currentUser.userId}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userId: currentUser.userId, items }),
    });
    if (!res.ok) {
      throw new Error(`Cart save failed: ${res.status}`);
    }
  }, [authenticatedFetch, currentUser?.userId]);

  const addToCart = useCallback(
    async (p: Product) => {
      setError(null);
      if (!currentUser?.userId) {
        setError("Please login first.");
        return;
      }
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
    [cart, currentUser?.userId, persistCart],
  );

  const checkout = useCallback(async () => {
    setCheckoutBusy(true);
    setCheckoutResult(null);
    setError(null);
    try {
      if (!currentUser?.userId || !authToken) {
        throw new Error("Please login first.");
      }
      if (cart.length === 0) {
        throw new Error("Cart is empty. Add a product first.");
      }

      const res = await authenticatedFetch("/api/checkout", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": newIdempotencyKey(),
        },
        body: JSON.stringify({
          userId: currentUser.userId,
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
  }, [authenticatedFetch, cart, currentUser?.userId, loadProducts]);

  const cartSummary = useMemo(
    () => cart.reduce((sum, c) => sum + c.quantity * c.unitPrice, 0),
    [cart],
  );

  const createProduct = useCallback(async () => {
    setCreateBusy(true);
    setError(null);
    setAdminMessage(null);
    try {
      const parsedPrice = Number.parseFloat(newProductPrice);
      if (!Number.isFinite(parsedPrice) || parsedPrice < 0) {
        throw new Error("Price must be a non-negative number.");
      }
      if (!newProductName.trim()) {
        throw new Error("Product name is required.");
      }

      const res = await authenticatedFetch("/api/products", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          productId: newProductId(),
          name: newProductName.trim(),
          description: newProductDescription.trim(),
          price: parsedPrice,
          isActive: true,
        }),
      });
      const body = (await res.json().catch(() => ({}))) as { error?: string };
      if (!res.ok) {
        throw new Error(body.error || `Create product failed: ${res.status}`);
      }

      setAdminMessage("Product created successfully.");
      setNewProductName("");
      setNewProductDescription("");
      setNewProductPrice("0");
      await loadAdminProducts();
      await loadProducts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Create product failed");
    } finally {
      setCreateBusy(false);
    }
  }, [
    authenticatedFetch,
    loadAdminProducts,
    loadProducts,
    newProductDescription,
    newProductName,
    newProductPrice,
  ]);

  return (
    <main>
      <h1>Online Store (React → HTTPS → Gateway)</h1>
      <p style={{ color: "#475569", marginTop: 0 }}>
        Demo user is pre-seeded in UserService. Cart is stored via CartService
        through the gateway so checkout validation matches the diagram.
      </p>

      {error ? <p className="error">{error}</p> : null}
      <section>
        <h2 style={{ marginTop: 0 }}>Page mode</h2>
        <div className="row-actions">
          <button type="button" disabled={!isAdminView} onClick={() => setIsAdminView(false)}>
            User view (catalog)
          </button>
          <button type="button" disabled={isAdminView} onClick={() => setIsAdminView(true)}>
            Admin view (manage products)
          </button>
        </div>
      </section>

      <section>
        <h2 style={{ marginTop: 0 }}>Login</h2>
        {loginMessage ? <p className="message">{loginMessage}</p> : null}
        <div className="form-grid">
          <label>
            Email
            <input type="email" value={loginEmail} onChange={(e) => setLoginEmail(e.target.value)} />
          </label>
          <label>
            Password
            <input type="password" value={loginPassword} onChange={(e) => setLoginPassword(e.target.value)} />
          </label>
          <div className="row-actions">
            <button type="button" disabled={loginBusy} onClick={() => void login()}>
              {loginBusy ? "Logging in..." : "Login"}
            </button>
            <button type="button" disabled={!authToken} onClick={logout}>
              Logout
            </button>
          </div>
        </div>
        {currentUser ? (
          <p>
            Logged in user: <strong>{currentUser.fullName}</strong> ({currentUser.email})
          </p>
        ) : null}
      </section>

      <section>
        <h2 style={{ marginTop: 0 }}>Register</h2>
        {registerMessage ? <p className="message">{registerMessage}</p> : null}
        <div className="form-grid">
          <label>
            Full name
            <input type="text" value={registerFullName} onChange={(e) => setRegisterFullName(e.target.value)} />
          </label>
          <label>
            Email
            <input type="email" value={registerEmail} onChange={(e) => setRegisterEmail(e.target.value)} />
          </label>
          <label>
            Password
            <input type="password" value={registerPassword} onChange={(e) => setRegisterPassword(e.target.value)} />
          </label>
          <div className="row-actions">
            <button type="button" disabled={registerBusy} onClick={() => void register()}>
              {registerBusy ? "Registering..." : "Register"}
            </button>
          </div>
        </div>
      </section>

      {isAdminView ? (
        <section>
          <h2 style={{ marginTop: 0 }}>Admin - add product</h2>
          {!authToken ? <p>Login required to create products.</p> : null}
          {adminMessage ? <p className="message">{adminMessage}</p> : null}
          <div className="form-grid">
            <label>
              Product name
              <input type="text" value={newProductName} onChange={(e) => setNewProductName(e.target.value)} />
            </label>
            <label>
              Description
              <input
                type="text"
                value={newProductDescription}
                onChange={(e) => setNewProductDescription(e.target.value)}
              />
            </label>
            <label>
              Price
              <input type="number" min="0" step="0.01" value={newProductPrice} onChange={(e) => setNewProductPrice(e.target.value)} />
            </label>
            <div className="row-actions">
              <button type="button" disabled={createBusy || !authToken} onClick={() => void createProduct()}>
                {createBusy ? "Saving..." : "Add product"}
              </button>
              <button type="button" onClick={() => void loadAdminProducts()} disabled={adminLoading}>
                Refresh list
              </button>
            </div>
          </div>

          <h3>Admin product list</h3>
          {adminLoading ? (
            <p>Loading…</p>
          ) : (
            <ul>
              {adminProducts.map((p) => (
                <li key={p.productId}>
                  <span>
                    <strong>{p.name}</strong> — ${p.price.toFixed(2)} {!p.isActive ? "(inactive)" : ""}
                  </span>
                  <span>{p.productId}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : (
        <section>
          <h2 style={{ marginTop: 0 }}>User - view products</h2>
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
      )}

      <section>
        <h2 style={{ marginTop: 0 }}>Cart</h2>
        {!authToken ? <p>Login required to persist cart and checkout.</p> : null}
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
