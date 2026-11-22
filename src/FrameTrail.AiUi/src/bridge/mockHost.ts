import type {
  AiEnvStatusPayload,
  AiProviderKey,
  AiSettingsSummaryPayload,
  EditorContextPayload,
  HostToWebMessage,
  LocaleCode,
  ThemeMode,
  WebToHostMessage,
} from './protocol';
import { FALLBACK_STRINGS } from '../i18n/strings';

export interface MockHostController {
  dispatchFromHost: (msg: HostToWebMessage) => void;
  getSentMessages: () => WebToHostMessage[];
  clearSentMessages: () => void;
}

const DEFAULT_CONTEXT: EditorContextPayload = {
  playheadSec: 4.2,
  playheadLabel: '00:04.2',
  durationSec: 42.8,
  durationLabel: '00:42.8',
  selectedClipIndex: 0,
  clipCount: 2,
  canvasWidth: 1920,
  canvasHeight: 1080,
  hasPointer: true,
};

const DEFAULT_ENV: AiEnvStatusPayload = {
  sidecarOnline: true,
  sidecarStarting: false,
  hasAntigravityCli: true,
  hasClaudeCodeCli: false,
  hasCodexCli: true,
  hasOllamaServer: true,
  ollamaModels: ['qwen2.5-coder:14b', 'llama3.2:latest'],
};

const DEFAULT_SETTINGS: AiSettingsSummaryPayload = {
  provider: 'openai',
  modelName: 'gpt-4o',
  baseUrl: '',
  baseUrlPlaceholder: 'https://api.openai.com/v1',
  autoApply: false,
  hasApiKey: true,
  providerHasApiKey: {
    openai: true,
    anthropic: false,
    deepseek: false,
  },
};

function readInitialQueryParams(): { theme: ThemeMode; locale: LocaleCode } {
  if (typeof window === 'undefined') {
    return { theme: 'dark', locale: 'zh-Hans' };
  }
  const params = new URLSearchParams(window.location.search);
  const theme: ThemeMode = params.get('theme') === 'light' ? 'light' : 'dark';
  const locale: LocaleCode = params.get('lang') === 'en' ? 'en' : 'zh-Hans';
  return { theme, locale };
}

export function createMockHost(
  emitToWeb: (msg: HostToWebMessage) => void,
  options?: { autoRespondTurns?: boolean }
): MockHostController {
  const sentMessages: WebToHostMessage[] = [];
  const autoRespond = options?.autoRespondTurns ?? true;

  let settings: AiSettingsSummaryPayload = {
    ...DEFAULT_SETTINGS,
    providerHasApiKey: { ...DEFAULT_SETTINGS.providerHasApiKey },
  };
  let env: AiEnvStatusPayload = { ...DEFAULT_ENV, ollamaModels: [...DEFAULT_ENV.ollamaModels] };
  let context: EditorContextPayload = { ...DEFAULT_CONTEXT };
  let turnCounter = 0;
  let activeTurnTimers: ReturnType<typeof setTimeout>[] = [];
  let activeVoiceTimers: ReturnType<typeof setTimeout>[] = [];
  let voiceFinalSent = false;

  const clearTurnTimers = () => {
    activeTurnTimers.forEach((id) => clearTimeout(id));
    activeTurnTimers = [];
  };

  const clearVoiceTimers = () => {
    activeVoiceTimers.forEach((id) => clearTimeout(id));
    activeVoiceTimers = [];
  };

  const handleWebMessage = (msg: WebToHostMessage) => {
    sentMessages.push(msg);

    if (!autoRespond) {
      return;
    }

    switch (msg.type) {
      case 'ui.ready': {
        const { theme, locale } = readInitialQueryParams();
        emitToWeb({
          type: 'init',
          payload: {
            theme,
            locale,
            strings: FALLBACK_STRINGS[locale],
            context,
            settings,
            env,
          },
        });
        break;
      }

      case 'chat.newSession': {
        clearTurnTimers();
        emitToWeb({ type: 'session.cleared', payload: {} });
        break;
      }

      case 'chat.stop': {
        clearTurnTimers();
        break;
      }

      case 'timeline.seek': {
        const sec = Math.max(0, Math.min(context.durationSec, msg.payload.seconds));
        const mins = Math.floor(sec / 60);
        const rem = (sec % 60).toFixed(1).padStart(4, '0');
        context = {
          ...context,
          playheadSec: sec,
          playheadLabel: `${String(mins).padStart(2, '0')}:${rem}`,
        };
        emitToWeb({ type: 'context.updated', payload: context });
        break;
      }

      case 'settings.selectProvider': {
        const provider: AiProviderKey = msg.payload.provider;
        const defaults: Record<AiProviderKey, { model: string; placeholder: string }> = {
          openai: { model: 'gpt-4o', placeholder: 'https://api.openai.com/v1' },
          anthropic: { model: 'claude-sonnet-4-20250514', placeholder: 'https://api.anthropic.com' },
          deepseek: { model: 'deepseek-flash', placeholder: 'https://api.deepseek.com/v1' },
          ollama: { model: 'qwen2.5-coder:14b', placeholder: 'http://localhost:11434/v1' },
          claude_code_cli: { model: 'claude-code-cli', placeholder: 'Local Claude Code CLI' },
          codex_cli: { model: 'codex-cli', placeholder: 'Local OpenAI Codex CLI' },
          antigravity_cli: { model: 'agy-cli', placeholder: 'Local Google Antigravity CLI (agy)' },
        };
        const def = defaults[provider];
        settings = {
          ...settings,
          provider,
          modelName: msg.payload.modelOverride || def.model,
          baseUrlPlaceholder: def.placeholder,
          hasApiKey: Boolean(settings.providerHasApiKey[provider]),
        };
        emitToWeb({ type: 'settings.updated', payload: settings });
        break;
      }

      case 'settings.updateConfig': {
        const nextMap = { ...settings.providerHasApiKey };
        if (msg.payload.clearApiKey) {
          nextMap[settings.provider] = false;
        } else if (msg.payload.newApiKey && msg.payload.newApiKey.trim().length > 0) {
          nextMap[settings.provider] = true;
        }
        settings = {
          ...settings,
          modelName: msg.payload.modelName ?? settings.modelName,
          baseUrl: msg.payload.baseUrl ?? settings.baseUrl,
          hasApiKey: Boolean(nextMap[settings.provider]),
          providerHasApiKey: nextMap,
        };
        emitToWeb({ type: 'settings.updated', payload: settings });
        break;
      }

      case 'settings.toggleAutoApply': {
        settings = { ...settings, autoApply: msg.payload.autoApply };
        emitToWeb({ type: 'settings.updated', payload: settings });
        break;
      }

      case 'sidecar.start': {
        env = { ...env, sidecarStarting: true };
        emitToWeb({ type: 'env.updated', payload: env });
        const id = setTimeout(() => {
          env = { ...env, sidecarStarting: false, sidecarOnline: true };
          emitToWeb({ type: 'env.updated', payload: env });
        }, 450);
        activeTurnTimers.push(id);
        break;
      }

      case 'env.refresh': {
        emitToWeb({ type: 'env.updated', payload: env });
        break;
      }

      case 'changes.accept': {
        emitToWeb({
          type: 'turn.changes',
          payload: {
            turnId: msg.payload.turnId,
            state: 'applied',
            revision: 42,
            items: [
              {
                badge: '+',
                label: 'Zoom 1.8×',
                detail: '00:04.2 – 00:06.2',
                seekSeconds: 4.2,
                timeLabel: '00:04.2',
              },
            ],
          },
        });
        break;
      }

      case 'changes.reject':
      case 'changes.undo': {
        emitToWeb({
          type: 'turn.changes',
          payload: {
            turnId: msg.payload.turnId,
            state: 'reverted',
            items: [
              {
                badge: '+',
                label: 'Zoom 1.8×',
                detail: '00:04.2 – 00:06.2',
                seekSeconds: 4.2,
                timeLabel: '00:04.2',
              },
            ],
          },
        });
        break;
      }

      case 'chat.send': {
        clearTurnTimers();
        const turnId = `mock-turn-${++turnCounter}`;
        emitToWeb({
          type: 'turn.started',
          payload: { turnId, userPrompt: msg.payload.prompt, timestamp: Date.now() },
        });

        activeTurnTimers.push(
          setTimeout(() => {
            emitToWeb({
              type: 'turn.stream',
              payload: {
                turnId,
                kind: 'thinking',
                text: 'Scanning timeline clips and pointer coordinates near 00:04.2...',
              },
            });
          }, 80)
        );

        activeTurnTimers.push(
          setTimeout(() => {
            emitToWeb({
              type: 'turn.stream',
              payload: {
                turnId,
                kind: 'tool_start',
                toolName: 'inspect_timeline',
                text: 'Reading clips & pointer events',
              },
            });
          }, 180)
        );

        activeTurnTimers.push(
          setTimeout(() => {
            emitToWeb({
              type: 'turn.stream',
              payload: {
                turnId,
                kind: 'tool_end',
                toolName: 'inspect_timeline',
                text: 'Found 2 clips (42.8s), pointer active at 00:04.2',
                succeeded: true,
              },
            });
          }, 320)
        );

        activeTurnTimers.push(
          setTimeout(() => {
            const reply =
              `已在 **00:04.2** 至 **00:06.2** 添加 **1.8× 镜头放大**。\n` +
              `- 可点击时间点 00:04.2 跳转预览效果，并在下方卡片确认保留或还原。`;
            emitToWeb({
              type: 'turn.stream',
              payload: { turnId, kind: 'token', text: reply },
            });
            emitToWeb({
              type: 'turn.completed',
              payload: {
                turnId,
                replyText: reply,
                elapsedSeconds: 0.5,
                status: 'done',
                toolCalls: [
                  {
                    toolName: 'inspect_timeline',
                    summary: 'Found 2 clips (42.8s), pointer active at 00:04.2',
                    succeeded: true,
                  },
                ],
              },
            });
            emitToWeb({
              type: 'turn.changes',
              payload: {
                turnId,
                state: settings.autoApply ? 'applied' : 'previewing',
                revision: settings.autoApply ? 42 : undefined,
                items: [
                  {
                    badge: '+',
                    label: 'Zoom 1.8×',
                    detail: '00:04.2 – 00:06.2',
                    seekSeconds: 4.2,
                    timeLabel: '00:04.2',
                  },
                ],
              },
            });
          }, 480)
        );
        break;
      }

      case 'voice.start': {
        clearVoiceTimers();
        voiceFinalSent = false;
        const isEnglish = msg.payload?.lang?.toLowerCase().startsWith('en');
        const interimSample = isEnglish
          ? 'Zoom in at 00:04.2...'
          : '在 00:04.2 处添加放大运镜…';
        const finalSample = isEnglish
          ? 'Zoom in at 00:04.2'
          : '在 00:04.2 处添加 1.8× 放大运镜';

        emitToWeb({
          type: 'voice.state',
          payload: { state: 'listening' },
        });

        activeVoiceTimers.push(
          setTimeout(() => {
            emitToWeb({
              type: 'voice.transcript',
              payload: { text: interimSample, isFinal: false },
            });
          }, 50)
        );

        activeVoiceTimers.push(
          setTimeout(() => {
            voiceFinalSent = true;
            emitToWeb({
              type: 'voice.transcript',
              payload: { text: finalSample, isFinal: true },
            });
          }, 160)
        );
        break;
      }

      case 'voice.stop': {
        clearVoiceTimers();
        if (!voiceFinalSent) {
          voiceFinalSent = true;
          const { locale } = readInitialQueryParams();
          const finalSample =
            locale === 'en' ? 'Zoom in at 00:04.2' : '在 00:04.2 处添加 1.8× 放大运镜';
          emitToWeb({
            type: 'voice.transcript',
            payload: { text: finalSample, isFinal: true },
          });
        }
        emitToWeb({
          type: 'voice.state',
          payload: { state: 'stopped' },
        });
        break;
      }
    }
  };

  return {
    dispatchFromHost: emitToWeb,
    getSentMessages: () => [...sentMessages],
    clearSentMessages: () => {
      sentMessages.length = 0;
    },
    // Expose internal handler via closure return
    ...({ handleWebMessage } as unknown as object),
  };
}

export type MockHostWithHandler = MockHostController & {
  handleWebMessage: (msg: WebToHostMessage) => void;
};
