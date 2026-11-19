export type ThemeMode = 'light' | 'dark';
export type LocaleCode = 'zh-Hans' | 'en';

export type AiProviderKey =
  | 'openai'
  | 'anthropic'
  | 'deepseek'
  | 'ollama'
  | 'claude_code_cli'
  | 'codex_cli'
  | 'antigravity_cli';

export interface EditorContextPayload {
  playheadSec: number;
  playheadLabel: string;
  durationSec: number;
  durationLabel: string;
  selectedClipIndex: number;
  clipCount: number;
  canvasWidth: number;
  canvasHeight: number;
  hasPointer: boolean;
}

export interface AiEnvStatusPayload {
  sidecarOnline: boolean;
  sidecarStarting: boolean;
  sidecarError?: string;
  hasAntigravityCli: boolean;
  hasClaudeCodeCli: boolean;
  hasCodexCli: boolean;
  hasOllamaServer: boolean;
  ollamaModels: string[];
}

export interface AiSettingsSummaryPayload {
  provider: AiProviderKey;
  modelName: string;
  baseUrl: string;
  baseUrlPlaceholder: string;
  autoApply: boolean;
  hasApiKey: boolean;
  providerHasApiKey: Record<string, boolean>;
}

export interface InitPayload {
  theme: ThemeMode;
  locale: LocaleCode;
  strings: Record<string, string>;
  context: EditorContextPayload;
  settings: AiSettingsSummaryPayload;
  env: AiEnvStatusPayload;
}

export type TurnStreamKind = 'thinking' | 'tool_start' | 'tool_end' | 'token' | 'done' | 'error';

export interface TurnStreamEventPayload {
  turnId: string;
  kind: TurnStreamKind;
  text?: string;
  toolName?: string;
  succeeded?: boolean;
}

export interface ToolCallRecordPayload {
  toolName: string;
  summary: string;
  succeeded: boolean;
}

export interface TurnCompletedPayload {
  turnId: string;
  replyText: string;
  elapsedSeconds: number;
  status: 'done' | 'cancelled' | 'error';
  toolCalls: ToolCallRecordPayload[];
}

export interface TimelineChangeItemPayload {
  badge: string;
  label: string;
  detail: string;
  seekSeconds?: number;
  timeLabel: string;
}

export type TimelineChangesState = 'previewing' | 'applied' | 'reverted';

export interface TimelineChangesPayload {
  turnId: string;
  state: TimelineChangesState;
  revision?: number;
  items: TimelineChangeItemPayload[];
}

export type VoiceRecognitionState = 'listening' | 'stopped' | 'error';

export interface VoiceStatePayload {
  state: VoiceRecognitionState;
  error?: string;
  fallbackToWebSpeech?: boolean;
}

export interface VoiceTranscriptPayload {
  text: string;
  isFinal: boolean;
}

export type HostToWebMessage =
  | { type: 'init'; payload: InitPayload }
  | { type: 'theme.changed'; payload: { theme: ThemeMode } }
  | { type: 'context.updated'; payload: EditorContextPayload }
  | { type: 'env.updated'; payload: AiEnvStatusPayload }
  | { type: 'settings.updated'; payload: AiSettingsSummaryPayload }
  | { type: 'turn.started'; payload: { turnId: string; userPrompt: string; timestamp?: number } }
  | { type: 'turn.stream'; payload: TurnStreamEventPayload }
  | { type: 'turn.completed'; payload: TurnCompletedPayload }
  | { type: 'turn.changes'; payload: TimelineChangesPayload }
  | { type: 'session.cleared'; payload?: Record<string, never> }
  | { type: 'composer.focus'; payload?: Record<string, never> }
  | { type: 'voice.state'; payload: VoiceStatePayload }
  | { type: 'voice.transcript'; payload: VoiceTranscriptPayload };

export type WebToHostMessage =
  | { type: 'ui.ready' }
  | { type: 'chat.send'; payload: { prompt: string } }
  | { type: 'chat.stop' }
  | { type: 'chat.newSession' }
  | { type: 'panel.close' }
  | { type: 'timeline.seek'; payload: { seconds: number } }
  | { type: 'changes.accept'; payload: { turnId: string } }
  | { type: 'changes.reject'; payload: { turnId: string } }
  | { type: 'changes.undo'; payload: { turnId: string } }
  | { type: 'settings.selectProvider'; payload: { provider: AiProviderKey; modelOverride?: string } }
  | {
      type: 'settings.updateConfig';
      payload: {
        modelName?: string;
        baseUrl?: string;
        newApiKey?: string;
        clearApiKey?: boolean;
      };
    }
  | { type: 'settings.toggleAutoApply'; payload: { autoApply: boolean } }
  | { type: 'sidecar.start' }
  | { type: 'env.refresh' }
  | {
      type: 'editor.shortcut';
      payload: { key: string; ctrl: boolean; shift: boolean; alt: boolean };
    }
  | { type: 'voice.start'; payload?: { lang?: string } }
  | { type: 'voice.stop'; payload?: Record<string, never> };
