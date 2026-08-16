# Synopsis V1.3 AI Settings

Synopsis keeps AI optional. Browser, network, console, TLS and project diagnostics work with AI disabled.

## Open AI Admin

Click `SET` in the browser toolbar, then use the `SETTINGS` tab.

## Ollama Local

Choose:

- Provider: `Ollama Local`
- Endpoint: `http://localhost:11434/`
- API key: not required

Click `TEST CONNECTION`. Synopsis loads the models installed in Ollama and automatically prefers a coding-oriented model if no saved model is available.

## Ollama Remote / Cloud

Choose:

- Provider: `Ollama Remote / Cloud`
- Endpoint: for Ollama Cloud use `https://ollama.com/`
- API key: enter the key in the masked field

The key is displayed as `********` unless `SHOW` is clicked. It is stored separately from normal settings and encrypted with Windows DPAPI for the current Windows user.

Click `TEST CONNECTION`, choose the model, then `SAVE AI SETTINGS`.

## Safety

- Secret redaction remains mandatory before diagnostic context is sent to AI.
- AI is recommendation-only in V1; it does not edit project source.
- Clearing the stored key deletes the DPAPI-protected value.
- A remote API key is sent only in the HTTP `Authorization: Bearer` header to the configured Ollama endpoint.
