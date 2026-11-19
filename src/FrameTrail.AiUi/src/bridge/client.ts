import type { HostToWebMessage, WebToHostMessage } from './protocol';
import { createMockHost, type MockHostWithHandler } from './mockHost';

interface ChromeWebViewBridge {
  postMessage: (message: unknown) => void;
  addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void;
  removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void;
}

declare global {
  interface Window {
    chrome?: {
      webview?: ChromeWebViewBridge;
    };
  }
}

export type HostMessageListener = (message: HostToWebMessage) => void;

class BridgeClient {
  private listeners = new Set<HostMessageListener>();
  private mockHost: MockHostWithHandler | null = null;
  private webviewListenerAttached = false;

  public isWebView2(): boolean {
    return typeof window !== 'undefined' && Boolean(window.chrome?.webview);
  }

  public subscribe(listener: HostMessageListener): () => void {
    this.listeners.add(listener);
    this.ensureAttached();
    return () => {
      this.listeners.delete(listener);
    };
  }

  public post(message: WebToHostMessage): void {
    const webview = typeof window !== 'undefined' ? window.chrome?.webview : undefined;
    if (webview) {
      webview.postMessage(message);
      return;
    }

    const mock = this.ensureMockHost();
    mock.handleWebMessage(message);
  }

  public getMockHost(options?: { autoRespondTurns?: boolean }): MockHostWithHandler {
    if (!this.mockHost || options !== undefined) {
      this.mockHost = createMockHost(
        (msg) => this.dispatchIncoming(msg),
        options
      ) as MockHostWithHandler;
    }
    return this.mockHost;
  }

  public resetForTests(options?: { autoRespondTurns?: boolean }): MockHostWithHandler {
    this.mockHost = createMockHost(
      (msg) => this.dispatchIncoming(msg),
      options
    ) as MockHostWithHandler;
    return this.mockHost;
  }

  private dispatchIncoming(raw: unknown): void {
    let parsed: HostToWebMessage | null = null;
    if (typeof raw === 'string') {
      try {
        parsed = JSON.parse(raw) as HostToWebMessage;
      } catch {
        return;
      }
    } else if (raw && typeof raw === 'object' && 'type' in raw) {
      parsed = raw as HostToWebMessage;
    }

    if (!parsed) return;
    this.listeners.forEach((listener) => listener(parsed));
  }

  private ensureMockHost(): MockHostWithHandler {
    if (!this.mockHost) {
      this.mockHost = createMockHost((msg) => this.dispatchIncoming(msg)) as MockHostWithHandler;
    }
    return this.mockHost;
  }

  private ensureAttached(): void {
    if (typeof window === 'undefined') return;
    const webview = window.chrome?.webview;
    if (webview) {
      if (!this.webviewListenerAttached) {
        webview.addEventListener('message', this.onWebViewMessage);
        this.webviewListenerAttached = true;
      }
    } else {
      this.ensureMockHost();
    }
  }

  private onWebViewMessage = (event: MessageEvent): void => {
    this.dispatchIncoming(event.data);
  };
}

export const bridgeClient = new BridgeClient();
