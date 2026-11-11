-- Stock contact tables for trying SiftDB: "Person", "Address", "Phone", "Email" in the public schema.
--   psql -U postgres -h localhost -1 -v ON_ERROR_STOP=1 -f tools/seed-contacts.sql
-- Remove with:  drop table "Email", "Phone", "Address", "Person";
--
-- EF Core naming on purpose (PascalCase, so every name needs double quotes in Postgres), which is exactly the
-- case the identifier features in design section 3 are for.

create table "Person" (
    "PersonId"     serial primary key,
    "FirstName"    text not null,
    "LastName"     text not null,
    "DateOfBirth"  date,
    "CreatedAt"    timestamptz not null default now()
);

create table "Address" (
    "AddressId"    serial primary key,
    "PersonId"     int not null references "Person"("PersonId"),
    "AddressType"  text not null default 'home',
    "Line1"        text not null,
    "Line2"        text,
    "City"         text not null,
    "Region"       text,
    "PostalCode"   text,
    "Country"      text not null default 'GB',
    "IsPrimary"    boolean not null default false
);

create table "Phone" (
    "PhoneId"      serial primary key,
    "PersonId"     int not null references "Person"("PersonId"),
    "PhoneType"    text not null default 'mobile',
    "Number"       text not null,
    "IsPrimary"    boolean not null default false
);

create table "Email" (
    "EmailId"       serial primary key,
    "PersonId"      int not null references "Person"("PersonId"),
    "EmailType"     text not null default 'personal',
    "EmailAddress"  text not null,
    "IsPrimary"     boolean not null default false
);

create index "IX_Address_PersonId" on "Address" ("PersonId");
create index "IX_Phone_PersonId"   on "Phone"   ("PersonId");
create index "IX_Email_PersonId"   on "Email"   ("PersonId");
create unique index "UX_Email_EmailAddress" on "Email" (lower("EmailAddress"));

-- 100 people with deterministic names and dates.
insert into "Person" ("FirstName", "LastName", "DateOfBirth")
select
    (array['Alice','Bob','Carol','David','Eve','Frank','Grace','Heidi','Ivan','Judy'])[1 + n % 10],
    (array['Smith','Jones','Taylor','Brown','Wilson','Evans','Thomas','Roberts','Walker','Wright'])[1 + (n / 10) % 10],
    date '1960-01-01' + (n * 211 % 16000)
from generate_series(0, 99) n;

-- Everyone has a primary home address; every third person also has a work address.
insert into "Address" ("PersonId", "AddressType", "Line1", "Line2", "City", "Region", "PostalCode", "IsPrimary")
select p."PersonId", 'home', (10 + p."PersonId" * 7 % 190) || ' ' || (array['High Street','Church Road','Station Road','Park Lane','Mill Lane'])[1 + p."PersonId" % 5],
       case when p."PersonId" % 4 = 0 then 'Flat ' || (1 + p."PersonId" % 12) end,
       (array['London','Manchester','Leeds','Bristol','Glasgow','Cardiff'])[1 + p."PersonId" % 6],
       (array['Greater London','Greater Manchester','West Yorkshire','Bristol','Scotland','Wales'])[1 + p."PersonId" % 6],
       'AB' || (1 + p."PersonId" % 9) || ' ' || (1 + p."PersonId" % 9) || 'XY', true
from "Person" p;

insert into "Address" ("PersonId", "AddressType", "Line1", "City", "PostalCode")
select p."PersonId", 'work', 'Unit ' || (1 + p."PersonId" % 30) || ', Business Park', 'London', 'EC' || (1 + p."PersonId" % 4) || 'A 1BB'
from "Person" p where p."PersonId" % 3 = 0;

-- A primary mobile for everyone, plus a home landline for every second person.
insert into "Phone" ("PersonId", "PhoneType", "Number", "IsPrimary")
select p."PersonId", 'mobile', '07700 9' || lpad((p."PersonId" * 137 % 100000)::text, 5, '0'), true from "Person" p;

insert into "Phone" ("PersonId", "PhoneType", "Number")
select p."PersonId", 'landline', '020 7946 0' || lpad((p."PersonId" * 31 % 1000)::text, 3, '0') from "Person" p where p."PersonId" % 2 = 0;

-- A primary personal email for everyone, plus a work email for every third person.
insert into "Email" ("PersonId", "EmailType", "EmailAddress", "IsPrimary")
select p."PersonId", 'personal', lower(p."FirstName" || '.' || p."LastName" || p."PersonId") || '@example.com', true from "Person" p;

insert into "Email" ("PersonId", "EmailType", "EmailAddress")
select p."PersonId", 'work', lower(p."FirstName" || '.' || p."LastName" || p."PersonId") || '@work.example.com' from "Person" p where p."PersonId" % 3 = 0;
