import { JsonPipe, NgFor, NgIf } from '@angular/common';
import { Component } from '@angular/core';

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

type RegisterResponse = UserProfile & { error?: string };

const authStorageKey = 'online-store-auth';

@Component({
  selector: 'app-root',
  imports: [NgIf, NgFor, JsonPipe],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  loginEmail = 'demo@example.com';
  loginPassword = 'demo-password';
  registerEmail = '';
  registerPassword = '';
  registerFullName = '';
  loginBusy = false;
  registerBusy = false;
  loginMessage: string | null = null;
  registerMessage: string | null = null;
  refreshToken: string | null = null;
  authToken: string | null = null;
  currentUser: UserProfile | null = null;

  products: Product[] = [];
  cart: CartItem[] = [];
  searchTerm = '';
  selectedQuantities: Partial<Record<string, number>> = {};

  adminProducts: Product[] = [];
  adminLoading = false;
  adminMessage: string | null = null;
  adminForm: Product = {
    productId: '',
    name: '',
    description: '',
    price: 0,
    isActive: true
  };
  editingProductId: string | null = null;

  loading = true;
  error: string | null = null;
  checkoutResult: unknown = null;
  checkoutBusy = false;

  constructor() {
    this.restoreAuthSession();
    void this.loadProducts();
    void this.loadAdminProducts();
  }

  get isAuthenticated(): boolean {
    return !!this.authToken && !!this.currentUser;
  }

  get authHeader(): string | null {
    return this.authToken ? `Bearer ${this.authToken}` : null;
  }

  get activeUserId(): string | null {
    return this.currentUser?.userId ?? null;
  }

  get cartSummary(): number {
    return this.cart.reduce((sum, c) => sum + c.quantity * c.unitPrice, 0);
  }

  async login(): Promise<void> {
    this.loginBusy = true;
    this.loginMessage = null;
    this.error = null;
    try {
      const res = await fetch('/api/users/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          email: this.loginEmail.trim(),
          password: this.loginPassword
        })
      });

      const body = (await res.json().catch(() => ({}))) as Partial<LoginResponse> & { error?: string };
      if (!res.ok || !body.accessToken || !body.user?.userId) {
        throw new Error(body.error || 'Invalid login credentials.');
      }

      this.authToken = body.accessToken;
      this.refreshToken = body.refreshToken ?? null;
      this.currentUser = body.user;
      this.persistAuthSession();
      this.loginMessage = `Logged in as ${body.user.fullName}`;
      this.cart = [];
      await this.loadProducts();
      await this.loadAdminProducts();
    } catch (e) {
      this.loginMessage = e instanceof Error ? e.message : 'Login failed';
    } finally {
      this.loginBusy = false;
    }
  }

  async register(): Promise<void> {
    this.registerBusy = true;
    this.registerMessage = null;
    try {
      const res = await fetch('/api/users/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          email: this.registerEmail.trim(),
          password: this.registerPassword,
          fullName: this.registerFullName.trim()
        })
      });

      const body = (await res.json().catch(() => ({}))) as RegisterResponse;
      if (!res.ok || !body.userId) {
        throw new Error(body.error || 'Registration failed.');
      }

      this.registerMessage = 'Registration successful. You can now login.';
      this.loginEmail = this.registerEmail.trim();
      this.loginPassword = this.registerPassword;
      this.registerFullName = '';
      this.registerEmail = '';
      this.registerPassword = '';
    } catch (e) {
      this.registerMessage = e instanceof Error ? e.message : 'Registration failed';
    } finally {
      this.registerBusy = false;
    }
  }

  logout(): void {
    void this.logoutRemote();
    this.authToken = null;
    this.refreshToken = null;
    this.currentUser = null;
    this.cart = [];
    this.loginMessage = 'Logged out.';
    localStorage.removeItem(authStorageKey);
  }

  async loadProducts(): Promise<void> {
    this.loading = true;
    this.error = null;
    try {
      const params = new URLSearchParams();
      if (this.searchTerm.trim()) {
        params.set('q', this.searchTerm.trim());
      }

      const query = params.toString();
      const res = await this.authenticatedFetch(query ? `/api/products?${query}` : '/api/products');
      if (!res.ok) {
        throw new Error(`Products failed: ${res.status}`);
      }
      this.products = (await res.json()) as Product[];
      this.syncSelectedQuantities();
    } catch (e) {
      this.error = e instanceof Error ? e.message : 'Failed to load products';
    } finally {
      this.loading = false;
    }
  }

  async addToCart(product: Product, quantity: number): Promise<void> {
    this.error = null;
    if (!this.activeUserId) {
      this.error = 'Please login first.';
      return;
    }

    if (quantity <= 0) {
      this.error = 'Quantity must be at least 1.';
      return;
    }

    const next = [...this.cart];
    const existing = next.find((x) => x.productId === product.productId);
    if (existing) {
      existing.quantity += quantity;
    } else {
      next.push({
        productId: product.productId,
        quantity,
        unitPrice: product.price
      });
    }

    try {
      await this.persistCart(next);
      this.cart = next;
      this.selectedQuantities[product.productId] = 1;
    } catch (e) {
      this.error = e instanceof Error ? e.message : 'Cart error';
    }
  }

  async checkout(): Promise<void> {
    this.checkoutBusy = true;
    this.checkoutResult = null;
    this.error = null;
    try {
      if (!this.activeUserId || !this.authHeader) {
        throw new Error('Please login first.');
      }

      if (this.cart.length === 0) {
        throw new Error('Cart is empty. Add a product first.');
      }

      const res = await this.authenticatedFetch('/api/checkout', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Idempotency-Key': this.newIdempotencyKey()
        },
        body: JSON.stringify({
          userId: this.activeUserId,
          currency: 'USD',
          items: this.cart.map((c) => ({
            productId: c.productId,
            quantity: c.quantity,
            unitPrice: c.unitPrice
          }))
        })
      });

      const body = (await res.json().catch(() => ({}))) as { error?: string };
      if (!res.ok) {
        throw new Error(body.error || `Checkout failed: ${res.status}`);
      }

      this.checkoutResult = body;
      this.cart = [];
      await this.loadProducts();
    } catch (e) {
      this.error = e instanceof Error ? e.message : 'Checkout error';
    } finally {
      this.checkoutBusy = false;
    }
  }

  formatPrice(value: number): string {
    return value.toFixed(2);
  }

  onQuantityChange(productId: string, raw: string): void {
    const parsed = Number.parseInt(raw, 10);
    this.selectedQuantities[productId] = Number.isFinite(parsed) && parsed > 0 ? parsed : 1;
  }

  async onSearch(): Promise<void> {
    await this.loadProducts();
  }

  startCreateProduct(): void {
    this.editingProductId = null;
    this.adminMessage = null;
    this.adminForm = {
      productId: '',
      name: '',
      description: '',
      price: 0,
      isActive: true
    };
  }

  startEditProduct(product: Product): void {
    this.editingProductId = product.productId;
    this.adminMessage = null;
    this.adminForm = { ...product };
  }

  async saveProduct(): Promise<void> {
    this.adminMessage = null;
    if (!this.isAuthenticated) {
      this.adminMessage = 'Please login first.';
      return;
    }

    if (!this.adminForm.name.trim()) {
      this.adminMessage = 'Product name is required.';
      return;
    }
    if (this.adminForm.price < 0) {
      this.adminMessage = 'Price must be zero or higher.';
      return;
    }

    const isUpdate = !!this.editingProductId;
    const endpoint = isUpdate
      ? `/api/admin/products/${this.editingProductId}`
      : '/api/admin/products';
    const method = isUpdate ? 'PUT' : 'POST';

    const payload: Product = {
      productId: this.editingProductId ?? '',
      name: this.adminForm.name.trim(),
      description: this.adminForm.description.trim(),
      price: Number(this.adminForm.price),
      isActive: this.adminForm.isActive ?? true
    };

    try {
      const res = await this.authenticatedFetch(endpoint, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      const body = (await res.json().catch(() => ({}))) as { error?: string };
      if (!res.ok) {
        throw new Error(body.error || `Save product failed: ${res.status}`);
      }

      this.adminMessage = isUpdate ? 'Product updated.' : 'Product created.';
      this.startCreateProduct();
      await this.loadAdminProducts();
      await this.loadProducts();
    } catch (e) {
      this.adminMessage = e instanceof Error ? e.message : 'Failed to save product';
    }
  }

  async deactivateProduct(product: Product): Promise<void> {
    this.adminMessage = null;
    if (!this.isAuthenticated) {
      this.adminMessage = 'Please login first.';
      return;
    }

    try {
      const res = await this.authenticatedFetch(`/api/admin/products/${product.productId}`, { method: 'DELETE' });
      if (!res.ok) {
        throw new Error(`Deactivate failed: ${res.status}`);
      }

      this.adminMessage = `Product "${product.name}" deactivated.`;
      await this.loadAdminProducts();
      await this.loadProducts();
    } catch (e) {
      this.adminMessage = e instanceof Error ? e.message : 'Failed to deactivate product';
    }
  }

  private async loadAdminProducts(): Promise<void> {
    if (!this.isAuthenticated) {
      this.adminProducts = [];
      this.adminLoading = false;
      return;
    }

    this.adminLoading = true;
    try {
      const res = await this.authenticatedFetch('/api/admin/products');
      if (!res.ok) {
        throw new Error(`Admin products failed: ${res.status}`);
      }
      this.adminProducts = (await res.json()) as Product[];
    } catch (e) {
      this.adminMessage = e instanceof Error ? e.message : 'Failed to load admin products';
    } finally {
      this.adminLoading = false;
    }
  }

  private syncSelectedQuantities(): void {
    const next: Partial<Record<string, number>> = {};
    for (const p of this.products) {
      next[p.productId] = this.selectedQuantities[p.productId] ?? 1;
    }
    this.selectedQuantities = next;
  }

  private async persistCart(items: CartItem[]): Promise<void> {
    if (!this.activeUserId) {
      throw new Error('Please login first.');
    }

    const res = await this.authenticatedFetch(`/api/carts/${this.activeUserId}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: this.activeUserId, items })
    });
    if (!res.ok) {
      throw new Error(`Cart save failed: ${res.status}`);
    }
  }

  private restoreAuthSession(): void {
    try {
      const raw = localStorage.getItem(authStorageKey);
      if (!raw) {
        return;
      }

      const parsed = JSON.parse(raw) as { accessToken?: string; refreshToken?: string; user?: UserProfile };
      if (!parsed.accessToken || !parsed.user?.userId || !parsed.refreshToken) {
        return;
      }

      this.authToken = parsed.accessToken;
      this.refreshToken = parsed.refreshToken;
      this.currentUser = parsed.user;
      this.loginEmail = parsed.user.email;
    } catch {
      localStorage.removeItem(authStorageKey);
    }
  }

  private persistAuthSession(): void {
    if (!this.authToken || !this.currentUser) {
      return;
    }

    localStorage.setItem(
      authStorageKey,
      JSON.stringify({
        accessToken: this.authToken,
        refreshToken: this.refreshToken,
        user: this.currentUser
      })
    );
  }

  private async authenticatedFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const mergedHeaders = new Headers(init?.headers ?? {});
    if (this.authHeader && !mergedHeaders.has('Authorization')) {
        mergedHeaders.set('Authorization', this.authHeader);
    }

    const response = await fetch(input, { ...init, headers: mergedHeaders });
    if (response.status !== 401 || !this.refreshToken) {
      return response;
    }

    const refreshed = await this.tryRefreshToken();
    if (!refreshed) {
      this.logout();
      return response;
    }

    const retryHeaders = new Headers(init?.headers ?? {});
    if (this.authHeader && !retryHeaders.has('Authorization')) {
      retryHeaders.set('Authorization', this.authHeader);
    }
    return fetch(input, { ...init, headers: retryHeaders });
  }

  private async tryRefreshToken(): Promise<boolean> {
    if (!this.refreshToken) {
      return false;
    }

    const res = await fetch('/api/users/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: this.refreshToken })
    });
    const body = (await res.json().catch(() => ({}))) as Partial<LoginResponse>;
    if (!res.ok || !body.accessToken || !body.refreshToken || !body.user?.userId) {
      return false;
    }

    this.authToken = body.accessToken;
    this.refreshToken = body.refreshToken;
    this.currentUser = body.user;
    this.persistAuthSession();
    return true;
  }

  private async logoutRemote(): Promise<void> {
    if (!this.refreshToken) {
      return;
    }

    try {
      await fetch('/api/users/logout', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: this.refreshToken })
      });
    } catch {
      // ignore network errors during logout
    }
  }

  private newIdempotencyKey(): string {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID();
    }

    return `checkout-${Date.now()}`;
  }
}
