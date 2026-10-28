# ADR 0007: Admin UI as an HTML view plus an ES module controller

**Status:** accepted · **Date:** 2026-10-28

## Context

jellyfin-web 12.0 (`viewContainer.js`) inserts plugin pages with `innerHTML`; inline `<script>` blocks only run on the legacy jQuery path. The supported path is `data-controller="__plugin/<PageName>"`, which imports `/web/configurationpage?name=<PageName>` as an ES module and calls its default export with the view element.

## Decision

`configPage.html` (markup and scoped CSS) plus `configPage.js` (module controller) are embedded resources registered through `IHasWebPages`. The controller uses `ApiClient.ajax` for authenticated JSON calls and builds every row with `textContent`, so filenames and titles can never inject HTML. No frontend framework or build step.
