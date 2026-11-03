// Finds TV boxes on the home network: every address of a /24 is asked for the phone remote's
// /api/hello (port 80), and the ones that answer with a box id are printed. For when tv.local
// does not resolve here (no mDNS in a container) and the box's address came from DHCP.
// Usage: node launcher/dev/linux/find-box.mjs <first three parts of the LAN, e.g. 192.168.1>
// (or HTPC_LAN in the environment). Prints "address box-id" per box; exit 1 when none answered.
const lan = process.argv[2] || process.env.HTPC_LAN;
if (!/^\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(lan || "")) {
  console.error("Usage: node find-box.mjs <a.b.c> (the LAN's first three numbers), or set HTPC_LAN");
  process.exit(2);
}

async function hello(address) {
  try {
    const r = await fetch(`http://${address}/api/hello`, { signal: AbortSignal.timeout(1500) });
    const j = await r.json();
    return typeof j.box === "string" ? j.box : null;
  } catch {
    return null;
  }
}

const addresses = Array.from({ length: 254 }, (_, i) => `${lan}.${i + 1}`);
const found = [];
// 32 at a time: the whole range in about 12 s, without flooding the network
for (let i = 0; i < addresses.length; i += 32) {
  const batch = addresses.slice(i, i + 32);
  const ids = await Promise.all(batch.map(hello));
  ids.forEach((id, k) => id && found.push(`${batch[k]} ${id}`));
}
found.forEach((line) => console.log(line));
process.exit(found.length ? 0 : 1);
