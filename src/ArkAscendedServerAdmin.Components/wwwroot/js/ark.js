// Small helpers the console panel and editors need from the browser side.
window.ark = {
  // Keeps a scrolling container pinned to its bottom while the reader has not scrolled up.
  scrollToBottom(element, force) {
    if (!element) {
      return;
    }
    const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
    if (force || distance < 48) {
      element.scrollTop = element.scrollHeight;
    }
  },
  isAtBottom(element) {
    if (!element) {
      return true;
    }
    return element.scrollHeight - element.scrollTop - element.clientHeight < 48;
  },
  focus(element) {
    if (element && typeof element.focus === "function") {
      element.focus();
    }
  },
  copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(() => true).catch(() => false);
    }
    return Promise.resolve(false);
  },
  // Keydown listeners attached per element (the RCON input, each INI textarea); releaseKeys removes them.
  _keyHandlers: new WeakMap(),
  _listen(element, handler) {
    if (!element || window.ark._keyHandlers.has(element)) {
      return;
    }
    window.ark._keyHandlers.set(element, handler);
    element.addEventListener("keydown", handler);
  },
  releaseKeys(element) {
    const handler = element && window.ark._keyHandlers.get(element);
    if (handler) {
      element.removeEventListener("keydown", handler);
      window.ark._keyHandlers.delete(element);
    }
  },
  // The console input handles these keys itself; Blazor cannot cancel the browser's default per key, so this does.
  // Up and down would move the caret to the ends of the text; Tab would leave the input while suggestions are open.
  filterConsoleKeys(element) {
    window.ark._listen(element, (e) => {
      if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) {
        return;
      }
      if (e.key === "ArrowUp" || e.key === "ArrowDown" || (e.key === "Tab" && element.dataset.suggesting === "true")) {
        e.preventDefault();
      }
    });
  },
  // Ctrl+S / Cmd+S in an INI textarea: never the browser's save dialog; the component decides whether to save.
  // The current text goes along so the save never runs on a value the input event has not delivered yet.
  bindSaveShortcut(element, component) {
    window.ark._listen(element, (e) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === "s" || e.key === "S")) {
        e.preventDefault();
        component.invokeMethodAsync("SaveFromShortcut", element.value).catch(() => {});
      }
    });
  },
};
