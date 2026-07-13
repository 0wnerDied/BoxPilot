# sing-box 1.13.14 Compatibility

BoxPilot treats the upstream `v1.13.14` option model as the compatibility
boundary. Native JSON profiles are authoritative: BoxPilot preserves the root
document, validates it with the installed core, retains its asset working
directory, and does not inject managed inbounds, DNS, routing, or experimental
options. Subscription and blank profiles remain BoxPilot-managed.

## Configuration Coverage

Native JSON import, the UTF-8 configuration editor, validation, execution, and
export cover every 1.13.14 root section. Selecting multiple JSON files invokes
the installed core's `merge` command before creating the profile:

- `log`, `dns`, `ntp`, and `certificate`
- `endpoints`, `inbounds`, and `outbounds`
- `route`, including inline, local, and remote rule sets
- `services` and `experimental`

Supported inbound types are `direct`, `mixed`, `socks`, `http`, `shadowsocks`,
`vmess`, `trojan`, `naive`, `hysteria`, `shadowtls`, `vless`, `tuic`,
`hysteria2`, `anytls`, `tun`, `redirect`, and `tproxy`.

Supported outbound types are `direct`, `block`, `socks`, `http`,
`shadowsocks`, `vmess`, `trojan`, `naive`, `hysteria`, `tor`, `ssh`,
`shadowtls`, `vless`, `tuic`, `hysteria2`, `anytls`, `selector`, and `urltest`.
WireGuard and Tailscale use the 1.13.14 `endpoints` model.

DNS transports include UDP, TCP, TLS, HTTPS, QUIC, HTTP/3, local, hosts,
FakeIP, DHCP, Tailscale, and systemd-resolved where the target build and OS
provide them. Route and DNS rules preserve all match fields and final or
non-final actions, including route, route-options, direct, bypass, reject,
hijack-dns, sniff, and resolve.

Service configurations for DERP, resolved, SSM API, CCM, OCM, and the OOM
killer pass through unchanged. Clash API, V2Ray statistics API, cache,
certificate providers, ACME, TLS/ECH/Reality, multiplexing, UDP-over-TCP,
TCP Brutal, and V2Ray transports are likewise native configuration fields.

## Commands and Assets

The Toolbox accepts the complete command tail after `sing-box`, including
`check`, `format`, `generate`, `geoip`, `geosite`, `merge`, `rule-set`, and
`tools`. BoxPilot reserves `run` because process ownership, logs, system proxy,
TUN authorization, and shutdown must stay coordinated with the desktop app.

Imported configurations keep the source directory as `-D` semantics for
relative rule sets, certificates, keys, Tor data, and other assets. The same
directory is passed to normal and privileged TUN execution.

## Platform Constraints

Compatibility does not override upstream build tags or operating-system
limits. For example, redirect/TProxy and some service features are
platform-specific. A feature is available when it exists in the installed
sing-box build and `sing-box check` accepts the configuration on that machine.

Removed 1.13 options are intentionally not revived: legacy inbound sniff
fields and the DNS outbound must use rule actions, while legacy WireGuard
outbounds must migrate to endpoints.
