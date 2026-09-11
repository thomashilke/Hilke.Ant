# Hilke.Ant

A .NET 8 library for talking to ANT+ sensors (heart rate straps, bike power meters, smart
trainers) through an ANTUSB-m style USB stick.

See the project README (at the repository root) for an overview, getting-started snippets, and
the list of sample programs. This site documents the public API surface of the four `src/` libraries:

- **Hilke.Ant.Plus** — the supported client surface: `AntPlusNode`, per-profile monitors, calibration.
- **Hilke.Ant.Transport.Serial** — `SerialAntTransport`, for opening the USB dongle.
- **Hilke.Ant.Testing** — `InMemoryAntTransport` / `SimulatedAntRadio`, a deterministic device double for tests.
- **Hilke.Ant** — the core ANT protocol layer. Its own public surface is intentionally minimal
  (`ChannelId`, `AntMessageId`, `ChannelResponseCode`, `IAntTransport`); everything else is
  internal implementation detail reached only through `Hilke.Ant.Plus`.

Start at [the API index](api/index.md).
