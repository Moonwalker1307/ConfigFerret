// Small helpers the Blazor page calls. Everything else runs on the server.
window.configFerret = {
  // Ctrl+B or Cmd+B hides or shows the sidebar, as in the desktop app; Escape closes a dialog.
  registerShortcuts(page) {
    const onKey = (e) => {
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'b') {
        e.preventDefault();
        page.invokeMethodAsync('ToggleSidebar');
      } else if (e.key === 'Escape') {
        page.invokeMethodAsync('Escape');
      }
    };
    document.addEventListener('keydown', onKey);
    return { dispose: () => document.removeEventListener('keydown', onKey) };
  },

  // Saves a report the server streams to the browser.
  async download(name, stream) {
    const buffer = await stream.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: name.endsWith('.json') ? 'application/json' : 'text/plain' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  },
};
