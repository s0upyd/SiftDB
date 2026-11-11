-- Demo data for trying SiftDB against a local Postgres. Safe to re-run; everything lives in the "shop" schema.
--   psql -U postgres -h localhost -f tools/seed-demo.sql
-- Remove with:  drop schema shop cascade;
--
-- Names are deliberately mixed: EF Core style "OrderItems" next to snake_case, so the casing and quoting
-- features (design section 3) have something to chew on.

drop schema if exists shop cascade;
create schema shop;

create table shop."Customers" (
    id          serial primary key,
    email       varchar(120) not null unique,
    "FullName"  text,
    created_at  timestamptz not null default now()
);

create table shop.orders (
    id           serial primary key,
    customer_id  int not null references shop."Customers"(id),
    status       text not null default 'new',
    total        numeric(10,2) not null default 0,
    placed_at    timestamptz not null default now()
);

create table shop."OrderItems" (
    order_id  int not null references shop.orders(id),
    line      int not null,
    sku       text not null,
    qty       int not null default 1,
    price     numeric(10,2) not null,
    primary key (order_id, line)
);

create table shop.products (
    sku    text primary key,
    name   text not null,
    price  numeric(10,2) not null
);

create index orders_customer_idx on shop.orders (customer_id);
create index orderitems_sku_idx on shop."OrderItems" (sku);

create view shop.order_totals as
    select o.id as order_id, o."customer_id", sum(i.qty * i.price) as computed_total
    from shop.orders o join shop."OrderItems" i on i.order_id = o.id
    group by o.id, o."customer_id";

insert into shop."Customers" (email, "FullName")
select 'user' || n || '@example.com', 'Customer ' || n from generate_series(1, 50) n;

insert into shop.products
select 'SKU-' || n, 'Product ' || n, (n * 1.5)::numeric(10,2) from generate_series(1, 40) n;

insert into shop.orders (customer_id, status, total)
select 1 + (n % 50), (array['new','paid','shipped'])[1 + n % 3], 0 from generate_series(1, 500) n;

insert into shop."OrderItems"
select o.id, l, 'SKU-' || (1 + (o.id * l) % 40), 1 + (o.id + l) % 5, (1 + (o.id * l) % 40) * 1.5
from shop.orders o, generate_series(1, 3) l;

update shop.orders o set total = t.computed_total from shop.order_totals t where t.order_id = o.id;
