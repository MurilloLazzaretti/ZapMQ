/**
 * Puts a text on the clipboard. The browser only offers its clipboard to a page served over HTTPS
 * (or from the machine itself); over plain HTTP the text is copied the older way, through a
 * selection. True when it was copied.
 */
export async function copyText(text: string): Promise<boolean> {
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Refused by the browser: the older way may still work.
    }
  }
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.position = 'fixed';
  area.style.top = '0';
  area.style.left = '0';
  area.style.opacity = '0';
  // Inside a dialog, the dialog keeps the focus to itself: the text has to be selected in it.
  const host = document.activeElement?.closest('.cdk-overlay-pane') ?? document.body;
  host.appendChild(area);
  const focused = document.activeElement as HTMLElement | null;
  area.focus();
  area.select();
  area.setSelectionRange(0, text.length);
  let copied = false;
  try {
    copied = document.execCommand('copy');
  } catch {
    copied = false;
  }
  area.remove();
  focused?.focus?.();
  return copied;
}
