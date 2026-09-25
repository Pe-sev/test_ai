const App = {
  token: null,
  isAdmin: false,

  async loadSession() {
    const res = await fetch('/api/session', { cache: 'no-store' });
    const data = await res.json();
    this.token = data.token;
    this.isAdmin = data.isAdmin;
    return data;
  },

  async send(method, url, body) {
    const options = { method, headers: { 'X-CSRF-TOKEN': this.token } };

    if (body instanceof FormData) {
      options.body = body;
    } else if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    return fetch(url, options);
  },

  async errorText(res) {
    try {
      const data = await res.json();
      if (data && data.error) return data.error;
    } catch {
      // Tom eller icke-JSON-kropp, t.ex. IIS egen felsida.
    }
    return `Error ${res.status}`;
  }
};
