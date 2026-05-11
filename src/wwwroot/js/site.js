window.bookingApi = (() => {
  let sessionNonce = "";

  async function postJson(url, body) {
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

    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      const error = new Error(payload.error || "request_failed");
      error.payload = payload;
      throw error;
    }

    return payload;
  }

  function setSessionNonce(nextSessionNonce) {
    sessionNonce = nextSessionNonce || "";
  }

  function browserSignals() {
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
