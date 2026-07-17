(() => {
  const validId = /^[A-Za-z0-9_-]{11}$/;
  let navigating = false;

  function videoIdFrom(href) {
    try {
      const id = new URL(href, location.origin).searchParams.get("v") || "";
      return validId.test(id) ? id : null;
    } catch {
      return null;
    }
  }

  function readSuggestions() {
    const results = [];
    const seen = new Set();
    const renderers = document.querySelectorAll("#related ytd-compact-video-renderer");
    for (const renderer of renderers) {
      const anchor = renderer.querySelector("a#thumbnail[href*='watch?v=']");
      const titleNode = renderer.querySelector("#video-title");
      const videoId = anchor ? videoIdFrom(anchor.href) : null;
      const title = titleNode?.textContent?.trim() || "";
      if (!videoId || !title || seen.has(videoId)) continue;
      seen.add(videoId);
      results.push({ videoId, title });
      if (results.length === 9) break;
    }
    return results;
  }

  async function publish() {
    const suggestions = readSuggestions();
    try { await chrome.runtime.sendMessage({ type: "mediadeck-state", suggestions }); } catch { }
  }

  async function poll() {
    if (navigating) return;
    try {
      const command = await chrome.runtime.sendMessage({ type: "mediadeck-poll" });
      if (validId.test(command?.videoId || "")) {
        navigating = true;
        location.assign(`https://www.youtube.com/watch?v=${command.videoId}`);
      }
    } catch { }
  }

  publish();
  setInterval(publish, 2500);
  setInterval(poll, 1000);
})();
