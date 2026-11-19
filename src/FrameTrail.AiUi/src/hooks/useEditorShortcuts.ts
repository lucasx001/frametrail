import { useEffect } from 'react';
import { bridgeClient } from '../bridge/client';

function isEditableElement(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName.toLowerCase();
  return tag === 'input' || tag === 'textarea' || tag === 'select';
}

function mapDomKeyToProtocolKey(event: KeyboardEvent): string | null {
  if (event.code === 'Space' || event.key === ' ') return 'Space';
  if (event.key === 'F11') return 'F11';
  if (event.key === 'ArrowLeft') return 'ArrowLeft';
  if (event.key === 'ArrowRight') return 'ArrowRight';
  if (event.key.toLowerCase() === 'z' && (event.ctrlKey || event.metaKey)) return 'Z';
  if (event.key.toLowerCase() === 'y' && (event.ctrlKey || event.metaKey)) return 'Y';
  return null;
}

export function useEditorShortcuts(): void {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const lower = event.key.toLowerCase();
      const ctrl = event.ctrlKey || event.metaKey;

      // Always block browser refresh / print shortcuts inside WebView2
      if (event.key === 'F5' || (ctrl && (lower === 'r' || lower === 'p'))) {
        event.preventDefault();
        return;
      }

      if (isEditableElement(event.target)) {
        return;
      }

      const mappedKey = mapDomKeyToProtocolKey(event);
      if (!mappedKey) return;

      // Only forward Ctrl+Z / Ctrl+Y when ctrl is held; Space / F11 / Arrows without ctrl
      if ((mappedKey === 'Z' || mappedKey === 'Y') && !ctrl) {
        return;
      }

      event.preventDefault();
      bridgeClient.post({
        type: 'editor.shortcut',
        payload: {
          key: mappedKey,
          ctrl,
          shift: event.shiftKey,
          alt: event.altKey,
        },
      });
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);
}
