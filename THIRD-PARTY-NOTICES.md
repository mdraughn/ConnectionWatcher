# Third-party notices

ConnectionWatcher is MIT-licensed (see [LICENSE](LICENSE)). It also ships
the following material from other sources.

## MAC vendor names (IEEE registry)

The device-maker names shown for discovered devices come from
[Libraries/ConInfrastructure/Discovery/oui.tsv](Libraries/ConInfrastructure/Discovery/oui.tsv),
which is generated from the IEEE Registration Authority's public MA-L
(OUI) listing, <https://standards-oui.ieee.org/oui/oui.csv>, by
[tools/Update-OuiTable.ps1](tools/Update-OuiTable.ps1). The script keeps
only the assignment and organization name, and recases names the registry
writes in all capitals. The table is embedded in the program; ConnectionWatcher
never contacts the IEEE (or anyone else) to look up a vendor.

## Pico CSS

The web UI bundles [Pico CSS](https://picocss.com) v2.1.1
(`Programs/ConWatch/wwwroot/css/pico.min.css`), Copyright 2019-2025,
licensed under the MIT License. Its licence header is kept in the file.

## Libraries

The .NET (NuGet) and web client (npm) packages ConnectionWatcher builds
on are listed in its project files and `package.json`, and are
distributed under their own licences.
