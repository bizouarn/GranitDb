-- Jeu de test GranitDb : base d'une boutique en ligne (SQLite).
PRAGMA foreign_keys = ON;

CREATE TABLE categories (
    id          INTEGER PRIMARY KEY,
    parent_id   INTEGER REFERENCES categories(id),
    name        TEXT NOT NULL UNIQUE,
    slug        TEXT NOT NULL UNIQUE
);

CREATE TABLE suppliers (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL,
    contact_email TEXT,
    country       TEXT NOT NULL
);

CREATE TABLE products (
    id           INTEGER PRIMARY KEY,
    sku          TEXT NOT NULL UNIQUE,
    name         TEXT NOT NULL,
    description  TEXT,
    category_id  INTEGER NOT NULL REFERENCES categories(id),
    supplier_id  INTEGER REFERENCES suppliers(id),
    unit_price   REAL NOT NULL CHECK (unit_price >= 0),
    vat_rate     REAL NOT NULL DEFAULT 0.20,
    is_active    INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT NOT NULL
);
CREATE INDEX ix_products_category ON products(category_id);
CREATE INDEX ix_products_supplier ON products(supplier_id);

CREATE TABLE warehouses (
    id    INTEGER PRIMARY KEY,
    name  TEXT NOT NULL UNIQUE,
    city  TEXT NOT NULL
);

CREATE TABLE stock (
    product_id    INTEGER NOT NULL REFERENCES products(id),
    warehouse_id  INTEGER NOT NULL REFERENCES warehouses(id),
    quantity      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (product_id, warehouse_id)
);

CREATE TABLE customers (
    id          INTEGER PRIMARY KEY,
    email       TEXT NOT NULL UNIQUE,
    first_name  TEXT NOT NULL,
    last_name   TEXT NOT NULL,
    phone       TEXT,
    birth_date  TEXT,
    created_at  TEXT NOT NULL
);

CREATE TABLE addresses (
    id           INTEGER PRIMARY KEY,
    customer_id  INTEGER NOT NULL REFERENCES customers(id),
    kind         TEXT NOT NULL CHECK (kind IN ('billing','shipping')),
    street       TEXT NOT NULL,
    postal_code  TEXT NOT NULL,
    city         TEXT NOT NULL,
    country      TEXT NOT NULL DEFAULT 'FR'
);
CREATE INDEX ix_addresses_customer ON addresses(customer_id);

CREATE TABLE orders (
    id                  INTEGER PRIMARY KEY,
    customer_id         INTEGER NOT NULL REFERENCES customers(id),
    shipping_address_id INTEGER REFERENCES addresses(id),
    billing_address_id  INTEGER REFERENCES addresses(id),
    status              TEXT NOT NULL CHECK (status IN ('pending','paid','shipped','delivered','cancelled')),
    ordered_at          TEXT NOT NULL,
    total_amount        REAL NOT NULL
);
CREATE INDEX ix_orders_customer ON orders(customer_id);
CREATE INDEX ix_orders_status ON orders(status);

CREATE TABLE order_items (
    order_id    INTEGER NOT NULL REFERENCES orders(id),
    line_no     INTEGER NOT NULL,
    product_id  INTEGER NOT NULL REFERENCES products(id),
    quantity    INTEGER NOT NULL CHECK (quantity > 0),
    unit_price  REAL NOT NULL,
    PRIMARY KEY (order_id, line_no)
);
CREATE INDEX ix_order_items_product ON order_items(product_id);

CREATE TABLE payments (
    id        INTEGER PRIMARY KEY,
    order_id  INTEGER NOT NULL REFERENCES orders(id),
    method    TEXT NOT NULL CHECK (method IN ('card','paypal','transfer')),
    amount    REAL NOT NULL,
    paid_at   TEXT NOT NULL
);
CREATE INDEX ix_payments_order ON payments(order_id);

CREATE TABLE reviews (
    id           INTEGER PRIMARY KEY,
    product_id   INTEGER NOT NULL REFERENCES products(id),
    customer_id  INTEGER NOT NULL REFERENCES customers(id),
    rating       INTEGER NOT NULL CHECK (rating BETWEEN 1 AND 5),
    comment      TEXT,
    created_at   TEXT NOT NULL,
    UNIQUE (product_id, customer_id)
);
