# Changelog

All notable changes to this project are documented here. The project follows Semantic Versioning once the public API reaches a stable release.

## 0.1.0 - 2026-07-19

First stable release:

- Published the supported communication libraries as stable NuGet packages.
- Finalized package metadata and consumer-facing documentation.
- Retained the validated public API, protocol implementations, Source Link and symbol packages from the preview release.

## 0.1.0-preview.1 - 2026-07-19

First installable preview:

- Added .NET Standard 2.1 abstractions, framing, checksums, bounded queues and reliable communication infrastructure.
- Added Serial, TCP client/server and UDP transports.
- Added monitoring, redaction, file history, export, replay and deterministic simulators.
- Added Modbus TCP/RTU, Siemens S7 ISO-on-TCP, Mitsubishi MC 3E Binary and OPC UA packages.
- Added unified PLC variables, batch reads/writes, monitoring and protocol adapters.
- Added digital IO, motion, barcode, weighing and private framed-device adapters.
- Added optional Microsoft.Extensions.DependencyInjection registrations.
- Added public API baselines, stress/failure tests, Source Link settings, symbols and package verification.

Known limitations and tested boundaries are listed in `docs/supported-features.md`.
