<script setup>
import { computed, onMounted, ref } from 'vue'

const authStorageKey = 'online-store-auth-vue'
const loginEmail = ref('demo@example.com')
const loginPassword = ref('demo-password')
const registerEmail = ref('')
const registerPassword = ref('')
const registerFullName = ref('')
const loginBusy = ref(false)
const registerBusy = ref(false)
const loginMessage = ref(null)
const registerMessage = ref(null)
const authToken = ref(null)
const refreshToken = ref(null)
const currentUser = ref(null)

const products = ref([])
const cart = ref([])
const loading = ref(true)
const error = ref(null)
const checkoutResult = ref(null)
const checkoutBusy = ref(false)

const isAuthenticated = computed(() => !!authToken.value && !!currentUser.value)

const cartSummary = computed(() =>
  cart.value.reduce((sum, item) => sum + item.quantity * item.unitPrice, 0),
)

function formatPrice(value) {
  return value.toFixed(2)
}

function newIdempotencyKey() {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID()
  }
  return `checkout-${Date.now()}`
}

async function loadProducts() {
  loading.value = true
  error.value = null
  try {
    const res = await authenticatedFetch('/api/products')
    if (!res.ok) {
      throw new Error(`Products failed: ${res.status}`)
    }
    products.value = await res.json()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load products'
  } finally {
    loading.value = false
  }
}

async function persistCart(items) {
  if (!currentUser.value?.userId) {
    throw new Error('Please login first.')
  }
  const res = await authenticatedFetch(`/api/carts/${currentUser.value.userId}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userId: currentUser.value.userId, items }),
  })
  if (!res.ok) {
    throw new Error(`Cart save failed: ${res.status}`)
  }
}

async function addToCart(product) {
  error.value = null
  if (!currentUser.value?.userId) {
    error.value = 'Please login first.'
    return
  }
  const next = [...cart.value]
  const existing = next.find((x) => x.productId === product.productId)
  if (existing) {
    existing.quantity += 1
  } else {
    next.push({ productId: product.productId, quantity: 1, unitPrice: product.price })
  }

  try {
    await persistCart(next)
    cart.value = next
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Cart error'
  }
}

async function checkout() {
  checkoutBusy.value = true
  checkoutResult.value = null
  error.value = null
  try {
    if (!currentUser.value?.userId || !authToken.value) {
      throw new Error('Please login first.')
    }

    if (cart.value.length === 0) {
      throw new Error('Cart is empty. Add a product first.')
    }

    const res = await authenticatedFetch('/api/checkout', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': newIdempotencyKey(),
      },
      body: JSON.stringify({
        userId: currentUser.value.userId,
        currency: 'USD',
        items: cart.value.map((c) => ({
          productId: c.productId,
          quantity: c.quantity,
          unitPrice: c.unitPrice,
        })),
      }),
    })

    const body = await res.json().catch(() => ({}))
    if (!res.ok) {
      throw new Error(body?.error || `Checkout failed: ${res.status}`)
    }

    checkoutResult.value = body
    cart.value = []
    await loadProducts()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Checkout error'
  } finally {
    checkoutBusy.value = false
  }
}

function persistAuthSession(token, nextRefreshToken, user) {
  localStorage.setItem(
    authStorageKey,
    JSON.stringify({
      accessToken: token,
      refreshToken: nextRefreshToken,
      user,
    }),
  )
}

function restoreAuthSession() {
  try {
    const raw = localStorage.getItem(authStorageKey)
    if (!raw) {
      return
    }
    const parsed = JSON.parse(raw)
    if (parsed?.accessToken && parsed?.refreshToken && parsed?.user?.userId) {
      authToken.value = parsed.accessToken
      refreshToken.value = parsed.refreshToken
      currentUser.value = parsed.user
      loginEmail.value = parsed.user.email
    }
  } catch {
    localStorage.removeItem(authStorageKey)
  }
}

async function tryRefreshToken() {
  if (!refreshToken.value) {
    return null
  }

  const res = await fetch('/api/users/refresh', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken: refreshToken.value }),
  })
  const body = await res.json().catch(() => ({}))
  if (!res.ok || !body?.accessToken || !body?.refreshToken || !body?.user?.userId) {
    return null
  }

  authToken.value = body.accessToken
  refreshToken.value = body.refreshToken
  currentUser.value = body.user
  persistAuthSession(body.accessToken, body.refreshToken, body.user)
  return body.accessToken
}

async function authenticatedFetch(url, init = {}) {
  const headers = new Headers(init.headers || {})
  if (authToken.value && !headers.has('Authorization')) {
    headers.set('Authorization', `Bearer ${authToken.value}`)
  }

  let response = await fetch(url, { ...init, headers })
  if (response.status !== 401) {
    return response
  }

  const refreshedAccessToken = await tryRefreshToken()
  if (!refreshedAccessToken) {
    return response
  }

  const retryHeaders = new Headers(init.headers || {})
  if (!retryHeaders.has('Authorization')) {
    retryHeaders.set('Authorization', `Bearer ${refreshedAccessToken}`)
  }
  response = await fetch(url, { ...init, headers: retryHeaders })
  return response
}

async function login() {
  loginBusy.value = true
  loginMessage.value = null
  try {
    const res = await fetch('/api/users/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: loginEmail.value.trim(), password: loginPassword.value }),
    })
    const body = await res.json().catch(() => ({}))
    if (!res.ok || !body?.accessToken || !body?.refreshToken || !body?.user?.userId) {
      throw new Error(body?.error || 'Invalid login credentials.')
    }
    authToken.value = body.accessToken
    refreshToken.value = body.refreshToken
    currentUser.value = body.user
    persistAuthSession(body.accessToken, body.refreshToken, body.user)
    loginMessage.value = `Logged in as ${body.user.fullName}`
    cart.value = []
  } catch (e) {
    loginMessage.value = e instanceof Error ? e.message : 'Login failed'
  } finally {
    loginBusy.value = false
  }
}

async function register() {
  registerBusy.value = true
  registerMessage.value = null
  try {
    const res = await fetch('/api/users/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: registerEmail.value.trim(),
        password: registerPassword.value,
        fullName: registerFullName.value.trim(),
      }),
    })
    const body = await res.json().catch(() => ({}))
    if (!res.ok || !body?.userId) {
      throw new Error(body?.error || 'Registration failed.')
    }
    registerMessage.value = 'Registration successful. You can now login.'
    loginEmail.value = registerEmail.value.trim()
    loginPassword.value = registerPassword.value
    registerEmail.value = ''
    registerPassword.value = ''
    registerFullName.value = ''
  } catch (e) {
    registerMessage.value = e instanceof Error ? e.message : 'Registration failed'
  } finally {
    registerBusy.value = false
  }
}

function logout() {
  if (refreshToken.value) {
    void fetch('/api/users/logout', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: refreshToken.value }),
    })
  }
  authToken.value = null
  refreshToken.value = null
  currentUser.value = null
  cart.value = []
  loginMessage.value = 'Logged out.'
  localStorage.removeItem(authStorageKey)
}

onMounted(async () => {
  restoreAuthSession()
  await loadProducts()
})
</script>

<template>
  <main>
    <h1>Online Store (Vue - Gateway)</h1>
    <p class="subtitle">
      Demo user is pre-seeded in UserService. Cart is stored via CartService through the gateway.
    </p>

    <p v-if="error" class="error">{{ error }}</p>

    <section>
      <h2>Login</h2>
      <p v-if="loginMessage" class="message">{{ loginMessage }}</p>
      <form class="login-form" @submit.prevent="login">
        <label>
          Email
          <input v-model="loginEmail" type="email" required />
        </label>
        <label>
          Password
          <input v-model="loginPassword" type="password" required />
        </label>
        <div class="row-actions">
          <button type="submit" :disabled="loginBusy">
            {{ loginBusy ? 'Logging in...' : 'Login' }}
          </button>
          <button type="button" :disabled="!isAuthenticated" @click="logout">Logout</button>
        </div>
      </form>
      <p v-if="isAuthenticated && currentUser">
        Logged in user: <strong>{{ currentUser.fullName }}</strong> ({{ currentUser.email }})
      </p>
    </section>

    <section>
      <h2>Register</h2>
      <p v-if="registerMessage" class="message">{{ registerMessage }}</p>
      <form class="login-form" @submit.prevent="register">
        <label>
          Full name
          <input v-model="registerFullName" type="text" required />
        </label>
        <label>
          Email
          <input v-model="registerEmail" type="email" required />
        </label>
        <label>
          Password
          <input v-model="registerPassword" type="password" required />
        </label>
        <div class="row-actions">
          <button type="submit" :disabled="registerBusy">
            {{ registerBusy ? 'Registering...' : 'Register' }}
          </button>
        </div>
      </form>
    </section>

    <section>
      <h2>Catalog</h2>
      <p v-if="loading">Loading...</p>
      <ul v-else>
        <li v-for="product in products" :key="product.productId">
          <span>
            <strong>{{ product.name }}</strong> - ${{ formatPrice(product.price) }}
          </span>
          <button type="button" @click="addToCart(product)">Add to cart</button>
        </li>
      </ul>
    </section>

    <section>
      <h2>Cart</h2>
      <p v-if="!isAuthenticated">Login required to persist cart and checkout.</p>
      <p v-if="cart.length === 0">Empty</p>
      <ul v-else>
        <li v-for="item in cart" :key="item.productId">
          <span>{{ item.productId }}</span>
          <span>qty {{ item.quantity }} @ ${{ formatPrice(item.unitPrice) }}</span>
        </li>
      </ul>
      <p><strong>Total:</strong> ${{ formatPrice(cartSummary) }}</p>
      <button type="button" :disabled="checkoutBusy" @click="checkout">
        {{ checkoutBusy ? 'Checking out...' : 'Checkout' }}
      </button>
    </section>

    <section v-if="checkoutResult">
      <h2>Last checkout</h2>
      <pre>{{ JSON.stringify(checkoutResult, null, 2) }}</pre>
    </section>
  </main>
</template>

<style scoped>
main {
  font-family: Inter, Arial, sans-serif;
  max-width: 900px;
  margin: 0 auto;
  padding: 1rem;
}

.subtitle {
  color: #475569;
  margin-top: 0;
}

section {
  border: 1px solid #e2e8f0;
  border-radius: 8px;
  padding: 1rem;
  margin-top: 1rem;
}

ul {
  list-style: none;
  padding-left: 0;
  margin: 0;
}

li {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.5rem 0;
}

button {
  cursor: pointer;
}

.message {
  color: #0f766e;
}

.error {
  color: #b91c1c;
}

pre {
  overflow: auto;
  background: #f8fafc;
  border-radius: 6px;
  padding: 0.75rem;
}

.login-form {
  display: grid;
  gap: 0.75rem;
  margin-bottom: 0.75rem;
}

.login-form label {
  display: grid;
  gap: 0.35rem;
  font-weight: 600;
}

input[type='email'],
input[type='password'],
input[type='text'] {
  border: 1px solid #cbd5e1;
  border-radius: 6px;
  padding: 0.4rem 0.5rem;
}

.row-actions {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}
</style>
