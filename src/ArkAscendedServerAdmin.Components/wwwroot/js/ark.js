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
};
