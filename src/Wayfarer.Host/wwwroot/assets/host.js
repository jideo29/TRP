(function () {
  var loading = document.getElementById("loading");
  var errorPanel = document.getElementById("error");
  var ready = document.getElementById("ready");
  var offline = document.getElementById("offline");
  var facts = document.getElementById("facts");
  var refusalButton = document.getElementById("read-refusal");

  function setOffline() {
    offline.hidden = navigator.onLine;
  }

  window.addEventListener("online", setOffline);
  window.addEventListener("offline", setOffline);
  setOffline();

  function show(state) {
    loading.hidden = state !== "loading";
    errorPanel.hidden = state !== "error";
    ready.hidden = state !== "ready";
    loading.setAttribute("aria-busy", state === "loading" ? "true" : "false");
  }

  function flag(value) {
    return value ? "True" : "False";
  }

  function addFact(label, value) {
    var wrap = document.createElement("div");
    var dt = document.createElement("dt");
    var dd = document.createElement("dd");
    dt.textContent = label;
    dd.textContent = value == null || value === "" ? "Not reported" : String(value);
    wrap.appendChild(dt);
    wrap.appendChild(dd);
    facts.appendChild(wrap);
  }

  function stamp() {
    var d = new Date();
    function pad(n) {
      return String(n).padStart(2, "0");
    }
    return (
      d.getFullYear() +
      "-" +
      pad(d.getMonth() + 1) +
      "-" +
      pad(d.getDate()) +
      " " +
      pad(d.getHours()) +
      ":" +
      pad(d.getMinutes())
    );
  }

  function processLabel(status) {
    if (status === "process-up") {
      return "Process up";
    }
    return status == null || status === "" ? "Not reported" : String(status);
  }

  async function load() {
    show("loading");
    facts.replaceChildren();
    try {
      var hostRes = await fetch("/api/host", { headers: { accept: "application/json" } });
      var healthRes = await fetch("/health", { headers: { accept: "application/json" } });
      if (!hostRes.ok || !healthRes.ok) {
        throw new Error(
          "The host returned " + hostRes.status + " for identity and " + healthRes.status + " for health."
        );
      }
      var host = await hostRes.json();
      var health = await healthRes.json();
      document.getElementById("chip-process").textContent = processLabel(health.status);
      document.getElementById("chip-money").textContent = flag(host.moneyPass);
      document.getElementById("chip-booked").textContent = "False";
      document.getElementById("chip-settled").textContent = "False";
      addFact("Product", host.product);
      addFact("Repository", host.repository);
      addFact("Role", host.role);
      addFact("Statement", host.statement);
      addFact("Boundary", host.tension);
      addFact("Distinct from the payments rail", flag(host.notPulse));
      addFact("Live payout", flag(host.livePayout));
      addFact("money_pass", flag(host.moneyPass));
      addFact("bank_booked", "False");
      addFact("settled", "False");
      addFact("Remittance journey unblocked", flag(host.remittanceJourneyUnblocked));
      addFact("Journey Accept claimed", flag(host.journeyAcceptClaimed));
      addFact("Intents absorbed by the payments rail", flag(host.novaRemittanceIntentsAbsorbedByPulse));
      addFact("Equicom", host.equicom);
      addFact("Outbound email", flag(host.outboundEmail));
      addFact("Sell-open", host.sellOpen);
      addFact("Process", processLabel(health.status));
      addFact("Health money_pass", flag(health.moneyPass));
      addFact("Health live payout", flag(health.livePayout));
      addFact("Health journey unblocked", flag(health.remittanceJourneyUnblocked));
      document.getElementById("read-at").textContent = "Read at " + stamp() + " browser local time.";
      show("ready");
    } catch (err) {
      document.getElementById("error-detail").textContent =
        err && err.message ? err.message : "The status could not be read.";
      show("error");
    }
  }

  function addRefusal(dl, label, value) {
    var dt = document.createElement("dt");
    var dd = document.createElement("dd");
    dt.textContent = label;
    dd.textContent = value == null ? "Not reported" : String(value);
    dl.appendChild(dt);
    dl.appendChild(dd);
  }

  async function readRefusal() {
    var box = document.getElementById("refusal");
    refusalButton.disabled = true;
    box.hidden = false;
    box.replaceChildren();
    var pending = document.createElement("p");
    pending.textContent = "Reading the refusal.";
    box.appendChild(pending);
    try {
      var res = await fetch("/api/remittance/payout", {
        method: "POST",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: "{}"
      });
      var body = await res.json();
      box.replaceChildren();
      var title = document.createElement("h3");
      title.textContent = "Refusal";
      var dl = document.createElement("dl");
      addRefusal(dl, "HTTP status", String(res.status));
      addRefusal(dl, "Code", body.code);
      addRefusal(dl, "Payout executed", body.payoutExecuted ? "True" : "False");
      addRefusal(dl, "money_pass", body.moneyPass ? "True" : "False");
      addRefusal(dl, "Message", body.message);
      box.appendChild(title);
      box.appendChild(dl);
    } catch (err) {
      box.replaceChildren();
      var fail = document.createElement("p");
      fail.textContent = err && err.message ? err.message : "The refusal could not be read.";
      box.appendChild(fail);
    } finally {
      refusalButton.disabled = false;
    }
  }

  document.getElementById("retry").addEventListener("click", load);
  refusalButton.addEventListener("click", readRefusal);
  load();
})();
