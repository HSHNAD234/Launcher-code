"use strict";
const crypto = require("node:crypto");
const path = require("node:path");
const express = require("express");
const { Pool } = require("pg");
const { DOMAINS, validateRules } = require("./lib/hosts");

const app = express();
const PORT = Number(process.env.PORT || 10000);
const DATABASE_URL = process.env.DATABASE_URL;
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || "";
const PRODUCTION = process.env.NODE_ENV === "production";
const COOKIE = PRODUCTION ? "__Host-hshonad-registry-admin" : "hshonad-registry-admin";
const SESSION_TTL = 8 * 60 * 60;

if (!DATABASE_URL) throw new Error("DATABASE_URL is required.");
if (Buffer.byteLength(ADMIN_TOKEN, "utf8") < 32) throw new Error("ADMIN_TOKEN must be at least 32 bytes.");

const pool = new Pool({
  connectionString: DATABASE_URL,
  ssl: process.env.DATABASE_SSL === "true" ? { rejectUnauthorized: true } : undefined,
  max: 5,
  idleTimeoutMillis: 30000,
  connectionTimeoutMillis: 10000
});

app.disable("x-powered-by");
app.set("trust proxy", 1);
app.use(express.json({ limit: "24kb" }));
app.use((req, res, next) => {
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("X-Frame-Options", "DENY");
  res.setHeader("Referrer-Policy", "no-referrer");
  res.setHeader("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'self'");
  next();
});
const SQL_DOMAINS = DOMAINS.map(d => "'" + d.replace(/'/g, "''") + "'").join(", ");

async function initializeDatabase() {
  await pool.query("CREATE TABLE IF NOT EXISTS host_registry (" +
    "domain text PRIMARY KEY CHECK (domain IN (" + SQL_DOMAINS + "))," +
    "enabled boolean NOT NULL DEFAULT false," +
    "purpose text NOT NULL DEFAULT '', note text NOT NULL DEFAULT ''," +
    "updated_at timestamptz NOT NULL DEFAULT now())");
  await pool.query("CREATE TABLE IF NOT EXISTS host_registry_audit (" +
    "id bigserial PRIMARY KEY, changed_at timestamptz NOT NULL DEFAULT now()," +
    "actor text NOT NULL DEFAULT 'admin', before_state jsonb NOT NULL, after_state jsonb NOT NULL)");
  for (const domain of DOMAINS) {
    await pool.query(
      "INSERT INTO host_registry(domain,enabled,purpose,note) VALUES($1,false,'','') ON CONFLICT(domain) DO NOTHING",
      [domain]
    );
  }
}
async function readRecords(client = pool) {
  const result = await client.query(
    "SELECT domain, enabled, purpose, note, updated_at AS \"updatedAt\" FROM host_registry " +
    "ORDER BY array_position($1::text[],domain)", [DOMAINS]);
  return result.rows;
}
function safeEqual(a, b) {
  const x = Buffer.from(String(a || ""), "utf8");
  const y = Buffer.from(String(b || ""), "utf8");
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}
function sign(expiry) {
  return crypto.createHmac("sha256", ADMIN_TOKEN).update("hshonad-registry:" + expiry).digest("hex");
}
function cookieValue(req) {
  for (const part of String(req.headers.cookie || "").split(";")) {
    const pos = part.indexOf("=");
    if (pos >= 0 && part.slice(0, pos).trim() === COOKIE)
      return decodeURIComponent(part.slice(pos + 1).trim());
  }
  return "";
}
function isAdmin(req) {
  const parts = cookieValue(req).split(".");
  if (parts.length !== 2) return false;
  const expiry = Number(parts[0]);
  return Number.isSafeInteger(expiry) && expiry > Math.floor(Date.now() / 1000) &&
    safeEqual(parts[1], sign(String(expiry)));
}
function requireAdmin(req, res, next) {
  if (!isAdmin(req)) return res.status(401).json({ ok: false, message: "กรุณาเข้าสู่ระบบผู้ดูแล" });
  next();
}
function sameOrigin(req, res, next) {
  const origin = req.get("origin");
  if (!origin) return next();
  try {
    const parsed = new URL(origin);
    if (parsed.host.toLowerCase() !== String(req.get("host") || "").toLowerCase() ||
      (PRODUCTION && parsed.protocol !== "https:"))
      return res.status(403).json({ ok: false, message: "Origin ไม่ได้รับอนุญาต" });
    next();
  } catch { res.status(403).json({ ok: false, message: "Origin ไม่ถูกต้อง" }); }
}
const loginAttempts = new Map();
function limitLogin(req, res, next) {
  const now = Date.now(), ip = String(req.ip || "unknown");
  const row = loginAttempts.get(ip);
  if (row && now - row.at < 900000 && row.count >= 12)
    return res.status(429).json({ ok: false, message: "ลองเข้าสู่ระบบมากเกินไป กรุณารอ 15 นาที" });
  if (!row || now - row.at >= 900000) loginAttempts.set(ip, { at: now, count: 1 });
  else row.count++;
  if (loginAttempts.size > 2000) {
    for (const [key, value] of loginAttempts)
      if (now - value.at >= 900000) loginAttempts.delete(key);
  }
  next();
}

app.get("/healthz", async (req, res) => {
  try { await pool.query("SELECT 1"); res.json({ ok: true, service: "hshonad-host-registry" }); }
  catch { res.status(503).json({ ok: false, service: "hshonad-host-registry" }); }
});
app.get("/", (req, res) => {
  res.setHeader("Cache-Control", "no-store");
  res.sendFile(path.join(__dirname, "public", "index.html"));
});
// Public read-only registry for the Launcher to display and diagnose.
app.get("/api/hosts-config", async (req, res) => {
  res.setHeader("Cache-Control", "no-store");
  try {
    const hosts = await readRecords();
    const updated = hosts.reduce((max, row) => {
      const date = row.updatedAt ? new Date(row.updatedAt) : new Date(0);
      return date > max ? date : max;
    }, new Date(0));
    res.json({ version: 1, updatedAt: updated.toISOString(), hosts });
  } catch { res.status(503).json({ ok: false, message: "อ่านทะเบียน Hosts ไม่สำเร็จ" }); }
});
app.get("/api/admin/session", (req, res) => {
  res.setHeader("Cache-Control", "no-store");
  res.json({ authenticated: isAdmin(req) });
});
app.post("/api/admin/login", sameOrigin, limitLogin, (req, res) => {
  if (!safeEqual(req.body && req.body.token, ADMIN_TOKEN))
    return res.status(401).json({ ok: false, message: "รหัสผู้ดูแลไม่ถูกต้อง" });
  loginAttempts.delete(String(req.ip || "unknown"));
  const expiry = String(Math.floor(Date.now() / 1000) + SESSION_TTL);
  res.cookie(COOKIE, expiry + "." + sign(expiry), {
    httpOnly: true, secure: PRODUCTION, sameSite: "strict", path: "/", maxAge: SESSION_TTL * 1000
  });
  res.json({ ok: true });
});
app.post("/api/admin/logout", sameOrigin, (req, res) => {
  res.clearCookie(COOKIE, { httpOnly: true, secure: PRODUCTION, sameSite: "strict", path: "/" });
  res.json({ ok: true });
});
app.get("/api/admin/hosts", requireAdmin, async (req, res) => {
  res.setHeader("Cache-Control", "no-store");
  try { res.json({ hosts: await readRecords() }); }
  catch { res.status(503).json({ ok: false, message: "อ่านทะเบียน Hosts ไม่สำเร็จ" }); }
});
app.put("/api/admin/hosts", sameOrigin, requireAdmin, async (req, res) => {
  let records;
  try { records = validateRules(req.body && req.body.hosts); }
  catch (error) { return res.status(400).json({ ok: false, message: error.message }); }
  let client;
  try {
    client = await pool.connect();
    await client.query("BEGIN");
    const before = await readRecords(client);
    for (const item of records) {
      await client.query(
        "UPDATE host_registry SET enabled=$2,purpose=$3,note=$4,updated_at=now() WHERE domain=$1",
        [item.domain, item.enabled, item.purpose, item.note]
      );
    }
    const after = await readRecords(client);
    await client.query(
      "INSERT INTO host_registry_audit(actor,before_state,after_state) VALUES($1,$2::jsonb,$3::jsonb)",
      ["admin", JSON.stringify(before), JSON.stringify(after)]
    );
    await client.query("COMMIT");
    res.json({ ok: true, hosts: after, message: "บันทึกทะเบียนโดเมนลงฐานข้อมูลแล้ว" });
  } catch {
    if (client) { try { await client.query("ROLLBACK"); } catch {} }
    res.status(503).json({ ok: false, message: "บันทึกฐานข้อมูลไม่สำเร็จ" });
  } finally { if (client) client.release(); }
});
app.get("/api/admin/audit", requireAdmin, async (req, res) => {
  try {
    const result = await pool.query(
      "SELECT id,changed_at AS \"changedAt\",actor,before_state AS \"beforeState\",after_state AS \"afterState\" " +
      "FROM host_registry_audit ORDER BY id DESC LIMIT 50"
    );
    res.json({ audit: result.rows });
  } catch { res.status(503).json({ ok: false, message: "อ่านประวัติไม่สำเร็จ" }); }
});
app.use((req, res) => res.status(404).json({ ok: false, message: "ไม่พบ Endpoint" }));
async function start() {
  await initializeDatabase();
  app.listen(PORT, "0.0.0.0", () => console.log("HSHONAD Host Registry listening on port " + PORT));
}
start().catch(error => {
  console.error("Failed to start Host Registry:", error.message);
  process.exit(1);
});
process.on("SIGTERM", async () => { try { await pool.end(); } finally { process.exit(0); } });
process.on("SIGINT", async () => { try { await pool.end(); } finally { process.exit(0); } });
