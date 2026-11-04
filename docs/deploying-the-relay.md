# Deploying the relay

Online play needs one relay server that every PC in the round can reach. The relay holds the lobbies (5-character
codes) and passes messages between the host's PC and the guests' PCs. It never reads the game's messages, keeps
nothing on disk, and logs only counts (lobbies and connected PCs), never codes, names or addresses.

One relay serves any number of rounds, so a group of friends only needs one, deployed once by any of them. Everyone
then enters its address in **Play Online**, for example `wss://my-golf-relay.fly.dev/ws`.

## Contents

- [What it needs](#what-it-needs)
- [Option 1: Fly.io](#option-1-flyio)
- [Option 2: any server with Docker](#option-2-any-server-with-docker)
- [Option 3: on your own PC, for testing](#option-3-on-your-own-pc-for-testing)
- [Checking it works](#checking-it-works)
- [Settings](#settings)
- [Updating and restarting](#updating-and-restarting)
- [Security notes](#security-notes)
- [Troubleshooting](#troubleshooting)

## What it needs

- **One machine, always on, reachable from the internet over HTTPS.** Lobbies live in memory, so it must be a single
  instance: two copies behind a load balancer would split the lobbies between them.
- **TLS (`wss://`).** The game only accepts plain `ws://` for a relay on the same PC or local network (or in a debug
  build). Fly.io provides TLS by itself; on your own server, put a reverse proxy with a certificate in front.
- **Very little hardware.** 256 MB of memory and a shared CPU are plenty for a few friends. Each shot is about 16 KB.
- **To build it:** Docker, or the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The relay is
  `src/GrimesGolf.Relay`, a small ASP.NET app with no other project references, so its folder builds on its own.

It listens on port 8080 inside the container and has three endpoints:

| Path | What it is |
|---|---|
| `/` | A line of text: `Grimes Golf relay` |
| `/health` | Counts, e.g. `lobbies=1 pcs=3` |
| `/ws` | The WebSocket the game connects to |

## Option 1: Fly.io

[Fly.io](https://fly.io) runs the Docker image on a small machine with HTTPS and a `*.fly.dev` address. The folder
already has a `fly.toml` set up for one always-on machine (`auto_stop_machines = "off"`, `min_machines_running = 1`,
256 MB). Check Fly.io's current pricing before you start; a machine left running is billed.

1. Install `flyctl` (see [Fly.io's install guide](https://fly.io/docs/flyctl/install/)) and sign in:

   ```bash
   fly auth login
   ```

2. From the relay's folder, create the app without deploying it. App names are global on Fly.io, so pick your own
   (it becomes the address):

   ```bash
   cd src/GrimesGolf.Relay
   ```

   ```bash
   fly launch --copy-config --no-deploy --name my-golf-relay
   ```

   `--copy-config` keeps the settings in `fly.toml` (it writes your app name into it). Choose a region close to the
   players when asked, or change `primary_region` in `fly.toml` (`ord` is Chicago).

3. Deploy it as a **single** machine (Fly.io otherwise starts two for availability, which would split the lobbies):

   ```bash
   fly deploy --ha=false
   ```

4. Make sure exactly one machine is running:

   ```bash
   fly scale count 1
   ```

5. The relay's address is `wss://<your-app-name>.fly.dev/ws`. Check it as described in
   [Checking it works](#checking-it-works).

`fly.toml` sets `Relay__TrustFlyClientIp = "true"`, so the per-address join limit uses the player's real address
(Fly.io passes it in the `Fly-Client-IP` header) rather than Fly.io's proxy. Don't set it anywhere else: outside
Fly.io that header could be made up.

To use your own domain instead of `*.fly.dev`, see `fly certs add` in Fly.io's documentation.

## Option 2: any server with Docker

Any always-on Linux machine with Docker works: a small cloud server, or a home server with a port opened for HTTPS.

1. Copy the repository (or just `src/GrimesGolf.Relay`) to the server and build the image from the relay's folder:

   ```bash
   cd src/GrimesGolf.Relay
   ```

   ```bash
   docker build -t grimes-golf-relay .
   ```

2. Run it, restarting by itself after a reboot. Bind it to the local machine only, so it's reached through the
   reverse proxy in the next step:

   ```bash
   docker run -d --name grimes-golf-relay --restart unless-stopped -p 127.0.0.1:8080:8080 grimes-golf-relay
   ```

3. Put a reverse proxy with a TLS certificate in front of it. With [Caddy](https://caddyserver.com), which gets the
   certificate by itself and passes WebSockets through, a whole `Caddyfile` is:

   ```
   relay.example.com {
       reverse_proxy 127.0.0.1:8080
   }
   ```

   Point `relay.example.com` at the server in DNS and open ports 80 and 443. nginx and others work too, as long as
   they pass the WebSocket `Upgrade` and `Connection` headers through and don't time out idle connections in under a
   minute (the relay pings every 15 seconds).

4. The relay's address is `wss://relay.example.com/ws`.

## Option 3: on your own PC, for testing

For trying online play with two copies of the game on one PC (see the
[development guide](development.md#online-play-on-one-pc)), with the .NET 10 SDK:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/GrimesGolf.Relay
```

The address is `ws://127.0.0.1:5080/ws`. To let another PC on the same network join, listen on all addresses
(`ASPNETCORE_URLS=http://0.0.0.0:5080`) and use this PC's local address, e.g. `ws://192.168.1.20:5080/ws`; allow
the port through Windows Firewall if asked.

## Checking it works

Open the health page in a browser, or:

```bash
curl https://my-golf-relay.fly.dev/health
```

It should answer `lobbies=0 pcs=0`. Then host a lobby in the game: the lobby shows its code once the relay has
answered, and `/health` shows `lobbies=1 pcs=1`.

## Settings

The limits suit a few friends, not the public. Each can be changed with an environment variable (in `fly.toml`
under `[env]`, or `docker run -e`), named `Relay__` plus the setting. Times are written `hh:mm:ss`.

| Setting | Default | What it does |
|---|---|---|
| `Relay__MaxLobbies` | `100` | Lobbies open at once |
| `Relay__MaxPcsPerLobby` | `4` | PCs in a lobby, the host's included |
| `Relay__MaxFrameBytes` | `262144` | Largest message, in bytes (256 KB) |
| `Relay__MaxLobbyAge` | `12:00:00` | Longest a lobby lasts, host there or not |
| `Relay__HostGrace` | `00:10:00` | How long a lobby waits for its host to come back |
| `Relay__JoinsPerWindow` | `20` | Lobby creates and joins allowed per address in each window |
| `Relay__JoinWindow` | `00:01:00` | The window for the limit above |
| `Relay__SendQueue` | `256` | Messages waiting for one PC before it's dropped as too slow |
| `Relay__HelloTimeout` | `00:00:15` | A connection that doesn't create or join a lobby by then is closed |
| `Relay__TrustFlyClientIp` | `false` | Take the player's address from Fly.io's `Fly-Client-IP` header (Fly.io only) |

Example, for a longer wait for the host:

```bash
docker run -d --name grimes-golf-relay --restart unless-stopped -p 127.0.0.1:8080:8080 -e Relay__HostGrace=00:30:00 grimes-golf-relay
```

## Updating and restarting

Lobbies are in memory, so a restart drops every connection. The game recovers by itself: every PC reconnects, the
host's PC asks for its old lobby code back, and the guests rejoin it. A round in progress carries on once the host
is back. Still, it's kinder to update between rounds.

- Fly.io: `fly deploy --ha=false` from `src/GrimesGolf.Relay`.
- Docker: rebuild the image, then `docker rm -f grimes-golf-relay` and the `docker run` command again.

The relay checks a protocol version when each PC connects. If a newer relay speaks a version an older game doesn't,
that game is told to update.

## Security notes

- **There's no login.** Anyone who knows the address can open a lobby, and anyone with a lobby's code can try to join
  it. The host still decides: a PC running a different app version, or arriving after the round has started, is
  turned away, and lobbies hold at most four PCs. Share the address and codes only with the people you play with.
- **Limits** (above) cap lobbies, message sizes and join attempts per address, and lobbies close by themselves.
- **No data is kept.** Nothing is written to disk, and the logs contain counts only.
- **Lobby codes** use 31 letters and digits that can't be mistaken for each other (no I, L, O, 0 or 1), so there are
  about 28.6 million codes; they're meant to be easy to read out, not secret.
- Each PC is known to the relay by a random secret token kept in its settings, so it can reconnect as itself; the
  host only ever sees a public id made from it.

## Troubleshooting

| Problem | What to check |
|---|---|
| The lobby never shows a code | The address: it must start `wss://` (or `ws://` for a local relay) and end in `/ws`. Open `/health` in a browser on that PC. |
| "Enter the relay's address" | The game didn't accept the address: plain `ws://` only works for a relay on the same PC or local network, or in a debug build. |
| "No lobby with that code" | The code (5 characters), and that the host's lobby is still open. A lobby closes 10 minutes after its host leaves. |
| "That lobby is full" | Four PCs are already in it. |
| "Too many attempts" | More than 20 creates or joins from one address in a minute. Wait a minute. |
| Different versions | Every PC must run the same build of the game. |
| Guests get split into different lobbies, or codes vanish | More than one relay instance is running. On Fly.io: `fly scale count 1`. |
| Connections drop every minute or so | A proxy in front of the relay is timing out idle WebSockets: raise its timeout above 60 seconds. |
