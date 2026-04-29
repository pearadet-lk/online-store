-- Online Store microservice schemas for Aurora PostgreSQL
-- Pattern: shared Aurora cluster, schema-per-service

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE SCHEMA IF NOT EXISTS user_service;
CREATE SCHEMA IF NOT EXISTS product_service;
CREATE SCHEMA IF NOT EXISTS cart_service;
CREATE SCHEMA IF NOT EXISTS order_service;
CREATE SCHEMA IF NOT EXISTS payment_service;
CREATE SCHEMA IF NOT EXISTS inventory_service;
CREATE SCHEMA IF NOT EXISTS shipping_service;
CREATE SCHEMA IF NOT EXISTS history_service;
CREATE SCHEMA IF NOT EXISTS gateway_service;

-- ============================================================================
-- User Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS user_service.users (
    user_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email VARCHAR(255) UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    full_name VARCHAR(200),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_user_email ON user_service.users (email);

CREATE TABLE IF NOT EXISTS user_service.refresh_tokens (
    token_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    token_hash VARCHAR(128) NOT NULL UNIQUE,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    replaced_by_token_hash VARCHAR(128) NULL
);

CREATE INDEX IF NOT EXISTS idx_user_refresh_tokens_user
    ON user_service.refresh_tokens (user_id, expires_at DESC);

CREATE INDEX IF NOT EXISTS idx_user_refresh_tokens_active
    ON user_service.refresh_tokens (token_hash, expires_at)
    WHERE revoked_at IS NULL;

-- ============================================================================
-- Product Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS product_service.products (
    product_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sku VARCHAR(64) UNIQUE NOT NULL,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    price NUMERIC(18, 2) NOT NULL CHECK (price >= 0),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_product_active_name
    ON product_service.products (is_active, name);

-- ============================================================================
-- Inventory Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS inventory_service.stock (
    product_id UUID PRIMARY KEY,
    available_qty INT NOT NULL CHECK (available_qty >= 0),
    reserved_qty INT NOT NULL DEFAULT 0 CHECK (reserved_qty >= 0),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inventory_service.inventory_reservations (
    reservation_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id UUID NOT NULL,
    product_id UUID NOT NULL,
    quantity INT NOT NULL CHECK (quantity > 0),
    status VARCHAR(30) NOT NULL CHECK (status IN ('Reserved', 'Released', 'Committed')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_inventory_reservation_order
    ON inventory_service.inventory_reservations (order_id);

-- ============================================================================
-- Cart Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS cart_service.carts (
    cart_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS cart_service.cart_items (
    cart_item_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    cart_id UUID NOT NULL,
    product_id UUID NOT NULL,
    quantity INT NOT NULL CHECK (quantity > 0),
    unit_price_snapshot NUMERIC(18, 2) NOT NULL CHECK (unit_price_snapshot >= 0),
    UNIQUE (cart_id, product_id)
);

CREATE INDEX IF NOT EXISTS idx_cart_items_cart
    ON cart_service.cart_items (cart_id);

-- ============================================================================
-- Order Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS order_service.orders (
    order_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    total_amount NUMERIC(18, 2) NOT NULL CHECK (total_amount >= 0),
    status VARCHAR(30) NOT NULL CHECK (status IN (
        'Pending',
        'InventoryReserved',
        'PaymentAuthorized',
        'Completed',
        'Cancelled',
        'Failed'
    )),
    saga_id UUID NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS order_service.order_items (
    order_item_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id UUID NOT NULL,
    product_id UUID NOT NULL,
    quantity INT NOT NULL CHECK (quantity > 0),
    unit_price NUMERIC(18, 2) NOT NULL CHECK (unit_price >= 0)
);

CREATE INDEX IF NOT EXISTS idx_order_user_created
    ON order_service.orders (user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_order_status
    ON order_service.orders (status);

-- Outbox pattern for reliable event publishing
CREATE TABLE IF NOT EXISTS order_service.outbox_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_type VARCHAR(100) NOT NULL,
    aggregate_id UUID NOT NULL,
    event_type VARCHAR(100) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Published', 'Failed')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_order_outbox_status_created
    ON order_service.outbox_events (status, created_at);

-- ============================================================================
-- Payment Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS payment_service.payments (
    payment_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id UUID NOT NULL UNIQUE,
    stripe_payment_intent_id VARCHAR(255),
    status VARCHAR(30) NOT NULL CHECK (status IN ('Pending', 'Authorized', 'Captured', 'Failed', 'Refunded')),
    amount NUMERIC(18, 2) NOT NULL CHECK (amount >= 0),
    currency CHAR(3) NOT NULL DEFAULT 'USD',
    failure_reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Idempotency request store (exactly-once semantics for charge endpoint)
CREATE TABLE IF NOT EXISTS payment_service.idempotency_requests (
    request_key VARCHAR(255) PRIMARY KEY,
    request_hash VARCHAR(128) NOT NULL,
    response_status_code INT NOT NULL,
    response_json JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_payment_idempotency_exp
    ON payment_service.idempotency_requests (expires_at);

-- ============================================================================
-- Shipping Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS shipping_service.shipments (
    shipment_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id UUID NOT NULL UNIQUE,
    carrier VARCHAR(100),
    tracking_number VARCHAR(100),
    status VARCHAR(30) NOT NULL CHECK (status IN ('Pending', 'Dispatched', 'InTransit', 'Delivered', 'Failed')),
    estimated_delivery_date DATE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================================
-- History Service
-- ============================================================================
CREATE TABLE IF NOT EXISTS history_service.order_history (
    history_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    order_id UUID NOT NULL,
    event_type VARCHAR(50) NOT NULL,
    event_payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_history_user_created
    ON history_service.order_history (user_id, created_at DESC);

-- ============================================================================
-- Gateway Service (checkout idempotency persistence)
-- ============================================================================
CREATE TABLE IF NOT EXISTS gateway_service.checkout_idempotency (
    scoped_key VARCHAR(512) PRIMARY KEY,
    request_hash VARCHAR(128) NOT NULL,
    in_progress BOOLEAN NOT NULL DEFAULT FALSE,
    response_status_code INT NULL,
    response_body TEXT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_gateway_checkout_idempotency_expires_at
    ON gateway_service.checkout_idempotency (expires_at);

-- ============================================================================
-- Cross-schema FK note:
-- In strict microservices, avoid cross-service FK constraints.
-- Keep referential integrity via service boundaries and events.
-- ============================================================================
