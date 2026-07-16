const companion = "http://127.0.0.1:43821";

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const source = sender.tab?.url || sender.url || "";
  if (!source.startsWith("https://www.youtube.com/watch")) {
    sendResponse({ ok: false });
    return false;
  }

  if (message?.type === "mediadeck-state") {
    const suggestions = Array.isArray(message.suggestions)
      ? message.suggestions.slice(0, 9).map(item => ({
          videoId: String(item.videoId || ""),
          title: String(item.title || "").slice(0, 120)
        }))
      : [];
    fetch(`${companion}/api/browser/youtube/state`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ suggestions })
    }).then(response => sendResponse({ ok: response.ok })).catch(() => sendResponse({ ok: false }));
    return true;
  }

  if (message?.type === "mediadeck-poll") {
    fetch(`${companion}/api/browser/youtube/command`, { cache: "no-store" })
      .then(response => response.ok ? response.json() : null)
      .then(command => sendResponse({ videoId: command?.videoId || null }))
      .catch(() => sendResponse({ videoId: null }));
    return true;
  }

  sendResponse({ ok: false });
  return false;
});
