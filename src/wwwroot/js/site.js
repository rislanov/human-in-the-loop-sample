window.bookingApi = (() => {
  // The frontend nonce is intentionally kept outside cookies. The server still
  // requires the HttpOnly verification-session cookie, but this header adds a
  // second same-page value that basic cross-site form posts cannot provide.
  let sessionNonce = "";

  async function postJson(url, body) {
    // All app APIs use JSON and same-origin credentials. Keeping this wrapper
    // small makes it easier to audit which requests carry the session nonce and
    // which errors are exposed back to the UI.
    const response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Accept": "application/json",
        ...(sessionNonce ? { "X-Booking-Session-Nonce": sessionNonce } : {})
      },
      credentials: "same-origin",
      body: JSON.stringify(body)
    });

    // Server errors are intentionally normalized to short error codes. The UI
    // can map them to friendly text without exposing detailed risk decisions or
    // implementation-specific bot-detection reasons.
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      const error = new Error(payload.error || "request_failed");
      error.payload = payload;
      throw error;
    }

    return payload;
  }

  function setSessionNonce(nextSessionNonce) {
    // The nonce is issued only after the email ticket is exchanged. A missing or
    // stale nonce should make protected endpoints fail closed on the server.
    sessionNonce = nextSessionNonce || "";
  }

  function browserSignals() {
    // These signals are not trusted as proof. They are cheap, coarse hints that
    // catch commodity automation and help the server detect inconsistent claims
    // such as touch telemetry from a non-touch browser.
    return {
      web_driver: Boolean(navigator.webdriver),
      plugins_length: navigator.plugins ? navigator.plugins.length : 0,
      languages_length: navigator.languages ? navigator.languages.length : 0,
      user_agent: navigator.userAgent || "",
      platform: navigator.platform || "",
      touch_capable: navigator.maxTouchPoints > 0
    };
  }

  return { postJson, setSessionNonce, browserSignals };
})();
