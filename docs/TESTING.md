# V1 Test Checklist

## Browser

- Launch Synopsis
- Search from the omnibox with plain words
- Navigate using a full URL
- Test localhost URL detection
- Open 3+ tabs
- Switch and close tabs
- Test Ctrl+L, Ctrl+T, Ctrl+W and F12

## Console

Open a test page and run:

```javascript
console.log('Synopsis test')
console.warn('Synopsis warning')
console.error('Synopsis error')
```

Confirm output appears in Console and warnings/errors also enter Error Centre.

Then run in the Synopsis console:

```javascript
document.title
```

## JavaScript exception

Run:

```javascript
setTimeout(() => { throw new Error('Synopsis exception test'); }, 10)
```

Confirm an Error Centre item appears.

## HTTP/network

Browse a site with normal resources and verify Network fills.

Request a known missing path and confirm HTTP 404 appears as a diagnostic.

Test a stopped local backend and confirm a network failure appears.

## TLS/security

Test:

- an HTTPS public site
- an HTTP localhost site
- a development host with an invalid/self-signed certificate if available

Check the Security + TLS tab for protocol, issuer, expiry and headers.

## Project linking

- Browse `http://localhost:8000`
- Link the local Laravel project folder
- Confirm framework shows Laravel
- Generate a Laravel log entry
- Confirm it appears in Error Centre

## Ollama

With Ollama stopped: confirm Synopsis displays AI OFFLINE and all browser tools still work.

With Ollama running and a model installed: refresh models, select an error and choose AI ANALYSE.

Confirm output contains a root cause, confidence, explanation, suggested fix and optional code.

## Secret redaction

Create a diagnostic containing fake values such as:

```text
Authorization: Bearer TEST_SECRET_123
password=TEST_PASSWORD
```

Use AI analysis and verify during debugger inspection/logging that the outgoing prompt contains `[REDACTED]` instead of the fake secrets.

## V1.5 incident correlation

Open **LAB** and click **RUN CORRELATED INCIDENT**. Confirm AI CODE REVIEW shows one incident containing both the console-error and JavaScript-exception signals rather than two unrelated review items.

Trigger a failed HTTP request that emits both an HTTP status diagnostic and a network/loading diagnostic. When Chromium provides the same request ID, confirm those signals share one incident.

## V1.5 source-aware code review

With the current site linked to its real source folder:

- select an incident in AI CODE REVIEW
- confirm the middle Source column shows the linked directory status
- confirm a resolved file path/excerpt is shown when Synopsis can resolve one
- if the generated asset has an adjacent Source Map v3 file, confirm the resolution label says `Source map:` and points at the original source location
- confirm recent linked server-log evidence is separately expandable
- run QUICK REVIEW and verify Ollama receives the incident plus source/log context
- run DEEP REVIEW and verify the same incident remains selected while a larger review is generated

## V1.5 failed HTTP response body

Trigger a `4xx` or `5xx` response that returns JSON/text. After the request finishes, confirm the incident gains an `HTTP ... response body` signal when Chromium makes the response body available. The response preview must be bounded; Synopsis should not grow the incident indefinitely for a huge response.
