import React, { createContext, useCallback, useContext, useEffect, useReducer, useRef } from 'react';
import { bridgeClient } from '../bridge/client';
import type {
  AiEnvStatusPayload,
  AiProviderKey,
  AiSettingsSummaryPayload,
  EditorContextPayload,
  HostToWebMessage,
  LocaleCode,
  ThemeMode,
  TimelineChangesPayload,
  ToolCallRecordPayload,
} from '../bridge/protocol';
import { FALLBACK_STRINGS, t as translate } from '../i18n/strings';

export type VoiceActivationMode = 'toggle' | 'hold';
export type VoiceLanguageOption = 'auto' | 'zh-CN' | 'en-US' | 'ja-JP';
export type VoiceEngineOption = 'native' | 'web';

export interface VoiceSettings {
  mode: VoiceActivationMode;
  language: VoiceLanguageOption;
  engine: VoiceEngineOption;
  autoSubmit: boolean;
}

export interface VoiceState {
  isListening: boolean;
  interimText: string;
  error: string | null;
  fallbackToWebSpeech: boolean;
}

export const VOICE_SETTINGS_STORAGE_KEY = 'frametrail.ai.voiceSettings';

export const DEFAULT_VOICE_SETTINGS: VoiceSettings = {
  mode: 'toggle',
  language: 'auto',
  engine: 'native',
  autoSubmit: false,
};

export function loadVoiceSettings(): VoiceSettings {
  if (typeof window === 'undefined') {
    return { ...DEFAULT_VOICE_SETTINGS };
  }
  try {
    const raw = window.localStorage.getItem(VOICE_SETTINGS_STORAGE_KEY);
    if (!raw) return { ...DEFAULT_VOICE_SETTINGS };
    const parsed = JSON.parse(raw) as Partial<VoiceSettings>;
    return {
      mode: parsed.mode === 'hold' ? 'hold' : 'toggle',
      language:
        parsed.language === 'zh-CN' ||
        parsed.language === 'en-US' ||
        parsed.language === 'ja-JP'
          ? parsed.language
          : 'auto',
      engine: parsed.engine === 'web' ? 'web' : 'native',
      autoSubmit: Boolean(parsed.autoSubmit),
    };
  } catch {
    return { ...DEFAULT_VOICE_SETTINGS };
  }
}

export function saveVoiceSettings(settings: VoiceSettings): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(VOICE_SETTINGS_STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // Ignore localStorage quota/privacy errors
  }
}

export interface TurnToolCallItem {
  toolName: string;
  summary: string;
  succeeded: boolean;
  running: boolean;
}

export interface CopilotTurn {
  id: string;
  userPrompt: string;
  thinkingText: string;
  thinkingDone: boolean;
  elapsedSeconds?: number;
  toolCalls: TurnToolCallItem[];
  streamMarkdown: string;
  finalMarkdown: string;
  status: 'streaming' | 'done' | 'cancelled' | 'error';
  changesCard?: TimelineChangesPayload;
}

export interface AiCopilotState {
  initialized: boolean;
  theme: ThemeMode;
  locale: LocaleCode;
  strings: Record<string, string>;
  context: EditorContextPayload;
  env: AiEnvStatusPayload;
  settings: AiSettingsSummaryPayload;
  turns: CopilotTurn[];
  isSending: boolean;
  promptHistory: string[];
  composerFocusNonce: number;
  settingsOpen: boolean;
  voiceSettings: VoiceSettings;
  voiceState: VoiceState;
}

function getInitialQueryTheme(): ThemeMode {
  if (typeof window === 'undefined') return 'dark';
  return new URLSearchParams(window.location.search).get('theme') === 'light' ? 'light' : 'dark';
}

function getInitialQueryLocale(): LocaleCode {
  if (typeof window === 'undefined') return 'zh-Hans';
  return new URLSearchParams(window.location.search).get('lang') === 'en' ? 'en' : 'zh-Hans';
}

const initialLocale = getInitialQueryLocale();

const INITIAL_STATE: AiCopilotState = {
  initialized: false,
  theme: getInitialQueryTheme(),
  locale: initialLocale,
  strings: FALLBACK_STRINGS[initialLocale],
  context: {
    playheadSec: 0,
    playheadLabel: '00:00.0',
    durationSec: 0,
    durationLabel: '00:00.0',
    selectedClipIndex: -1,
    clipCount: 0,
    canvasWidth: 1920,
    canvasHeight: 1080,
    hasPointer: false,
  },
  env: {
    sidecarOnline: false,
    sidecarStarting: false,
    hasAntigravityCli: false,
    hasClaudeCodeCli: false,
    hasCodexCli: false,
    hasOllamaServer: false,
    ollamaModels: [],
  },
  settings: {
    provider: 'openai',
    modelName: 'gpt-4o',
    baseUrl: '',
    baseUrlPlaceholder: 'https://api.openai.com/v1',
    autoApply: false,
    hasApiKey: false,
    providerHasApiKey: {},
  },
  turns: [],
  isSending: false,
  promptHistory: [],
  composerFocusNonce: 0,
  settingsOpen: false,
  voiceSettings: loadVoiceSettings(),
  voiceState: {
    isListening: false,
    interimText: '',
    error: null,
    fallbackToWebSpeech: false,
  },
};

type LocalAction =
  | { type: 'HOST_MESSAGE'; message: HostToWebMessage }
  | { type: 'PUSH_PROMPT_HISTORY'; prompt: string }
  | { type: 'SET_SETTINGS_OPEN'; open: boolean }
  | { type: 'SET_SIDECAR_STARTING'; starting: boolean }
  | { type: 'UPDATE_VOICE_SETTINGS'; partial: Partial<VoiceSettings> }
  | { type: 'SET_VOICE_STATE'; partial: Partial<VoiceState> };

function upsertToolCall(
  list: TurnToolCallItem[],
  toolName: string,
  summary: string,
  running: boolean,
  succeeded: boolean
): TurnToolCallItem[] {
  const idx = list.findIndex((item) => item.toolName === toolName);
  if (idx >= 0) {
    const next = [...list];
    next[idx] = { toolName, summary: summary || next[idx].summary, running, succeeded };
    return next;
  }
  return [...list, { toolName, summary, running, succeeded }];
}

function mergeFinalToolCalls(
  existing: TurnToolCallItem[],
  records: ToolCallRecordPayload[],
  status: CopilotTurn['status']
): TurnToolCallItem[] {
  const failed = status === 'error' || status === 'cancelled';
  let next = existing.map((item) =>
    item.running
      ? { ...item, running: false, succeeded: failed ? false : item.succeeded }
      : item
  );
  for (const tc of records) {
    next = upsertToolCall(next, tc.toolName, tc.summary, false, tc.succeeded);
  }
  return next;
}

function copilotReducer(state: AiCopilotState, action: LocalAction): AiCopilotState {
  if (action.type === 'PUSH_PROMPT_HISTORY') {
    const trimmed = action.prompt.trim();
    if (!trimmed) return state;
    return {
      ...state,
      promptHistory: [...state.promptHistory, trimmed],
    };
  }

  if (action.type === 'SET_SETTINGS_OPEN') {
    return { ...state, settingsOpen: action.open };
  }

  if (action.type === 'SET_SIDECAR_STARTING') {
    return {
      ...state,
      env: { ...state.env, sidecarStarting: action.starting },
    };
  }

  if (action.type === 'UPDATE_VOICE_SETTINGS') {
    const nextVoiceSettings: VoiceSettings = {
      ...state.voiceSettings,
      ...action.partial,
    };
    saveVoiceSettings(nextVoiceSettings);
    return {
      ...state,
      voiceSettings: nextVoiceSettings,
    };
  }

  if (action.type === 'SET_VOICE_STATE') {
    return {
      ...state,
      voiceState: {
        ...state.voiceState,
        ...action.partial,
      },
    };
  }

  const msg = action.message;
  switch (msg.type) {
    case 'init': {
      return {
        ...state,
        initialized: true,
        theme: msg.payload.theme,
        locale: msg.payload.locale,
        strings: { ...FALLBACK_STRINGS[msg.payload.locale], ...msg.payload.strings },
        context: msg.payload.context,
        settings: msg.payload.settings,
        env: msg.payload.env,
      };
    }

    case 'theme.changed': {
      return { ...state, theme: msg.payload.theme };
    }

    case 'context.updated': {
      return { ...state, context: msg.payload };
    }

    case 'env.updated': {
      return { ...state, env: msg.payload };
    }

    case 'settings.updated': {
      return { ...state, settings: msg.payload };
    }

    case 'turn.started': {
      const existing = state.turns.some((turn) => turn.id === msg.payload.turnId);
      if (existing) return { ...state, isSending: true };
      const newTurn: CopilotTurn = {
        id: msg.payload.turnId,
        userPrompt: msg.payload.userPrompt,
        thinkingText: '',
        thinkingDone: false,
        toolCalls: [],
        streamMarkdown: '',
        finalMarkdown: '',
        status: 'streaming',
      };
      return {
        ...state,
        isSending: true,
        turns: [...state.turns, newTurn],
      };
    }

    case 'turn.stream': {
      const { turnId, kind, text, toolName, succeeded } = msg.payload;
      const idx = state.turns.findIndex((turn) => turn.id === turnId);
      if (idx < 0) return state;

      const turn = state.turns[idx];
      let updated: CopilotTurn = turn;

      switch (kind) {
        case 'thinking':
          if (text) {
            updated = {
              ...turn,
              thinkingText: turn.thinkingText ? `${turn.thinkingText}\n${text}` : text,
            };
          }
          break;
        case 'tool_start':
          updated = {
            ...turn,
            toolCalls: upsertToolCall(
              turn.toolCalls,
              toolName || 'tool',
              text || '',
              true,
              true
            ),
          };
          break;
        case 'tool_end':
          updated = {
            ...turn,
            toolCalls: upsertToolCall(
              turn.toolCalls,
              toolName || 'tool',
              text || '',
              false,
              succeeded ?? true
            ),
          };
          break;
        case 'token':
          if (text) {
            updated = {
              ...turn,
              streamMarkdown: turn.streamMarkdown + text,
            };
          }
          break;
        case 'error':
          updated = {
            ...turn,
            thinkingDone: true,
            status: 'error',
            toolCalls: turn.toolCalls.map((item) =>
              item.running
                ? {
                    ...item,
                    running: false,
                    succeeded: false,
                    summary: text || item.summary,
                  }
                : item
            ),
            finalMarkdown: text || turn.streamMarkdown,
          };
          break;
        case 'done':
          updated = {
            ...turn,
            thinkingDone: true,
            status: 'done',
          };
          break;
      }

      const nextTurns = [...state.turns];
      nextTurns[idx] = updated;
      return {
        ...state,
        turns: nextTurns,
        isSending: kind === 'done' || kind === 'error' ? false : state.isSending,
      };
    }

    case 'turn.completed': {
      const { turnId, replyText, elapsedSeconds, status, toolCalls } = msg.payload;
      const idx = state.turns.findIndex((turn) => turn.id === turnId);
      if (idx < 0) {
        return { ...state, isSending: false };
      }

      const turn = state.turns[idx];
      const nextTurns = [...state.turns];
      nextTurns[idx] = {
        ...turn,
        thinkingDone: true,
        elapsedSeconds,
        status,
        toolCalls: mergeFinalToolCalls(turn.toolCalls, toolCalls || [], status),
        finalMarkdown: replyText || turn.streamMarkdown,
      };
      return {
        ...state,
        turns: nextTurns,
        isSending: false,
      };
    }

    case 'turn.changes': {
      const idx = state.turns.findIndex((turn) => turn.id === msg.payload.turnId);
      if (idx < 0) return state;
      const nextTurns = [...state.turns];
      nextTurns[idx] = {
        ...nextTurns[idx],
        changesCard: msg.payload,
      };
      return { ...state, turns: nextTurns };
    }

    case 'session.cleared': {
      return {
        ...state,
        turns: [],
        isSending: false,
      };
    }

    case 'composer.focus': {
      return {
        ...state,
        composerFocusNonce: state.composerFocusNonce + 1,
      };
    }

    case 'voice.state': {
      const { state: recognitionState, error, fallbackToWebSpeech } = msg.payload;
      return {
        ...state,
        voiceState: {
          ...state.voiceState,
          isListening: recognitionState === 'listening',
          interimText: recognitionState === 'listening' ? state.voiceState.interimText : '',
          error: recognitionState === 'error' ? error || null : null,
          fallbackToWebSpeech: Boolean(fallbackToWebSpeech),
        },
      };
    }

    case 'voice.transcript': {
      const { text, isFinal } = msg.payload;
      return {
        ...state,
        voiceState: {
          ...state.voiceState,
          interimText: isFinal ? '' : text,
          error: null,
        },
      };
    }

    default:
      return state;
  }
}

function applyThemeToDocument(theme: ThemeMode): void {
  if (typeof document === 'undefined') return;
  const root = document.documentElement;
  const isDark = theme === 'dark';
  root.classList.toggle('dark', isDark);
  root.style.colorScheme = isDark ? 'dark' : 'light';
  root.style.backgroundColor = isDark ? '#111113' : '#FFFFFF';
}

interface AiCopilotContextValue {
  state: AiCopilotState;
  tr: (key: string, ...args: Array<string | number>) => string;
  sendPrompt: (prompt: string) => void;
  stopGenerating: () => void;
  newSession: () => void;
  closePanel: () => void;
  seekTimeline: (seconds: number) => void;
  acceptChanges: (turnId: string) => void;
  rejectChanges: (turnId: string) => void;
  undoChanges: (turnId: string) => void;
  selectProvider: (provider: AiProviderKey, modelOverride?: string) => void;
  updateConfig: (config: {
    modelName?: string;
    baseUrl?: string;
    newApiKey?: string;
    clearApiKey?: boolean;
  }) => void;
  toggleAutoApply: (autoApply: boolean) => void;
  startSidecar: () => void;
  refreshEnv: () => void;
  setSettingsOpen: (open: boolean) => void;
  updateVoiceSettings: (partial: Partial<VoiceSettings>) => void;
  setVoiceState: (partial: Partial<VoiceState>) => void;
  emitVoiceTranscript: (text: string, isFinal: boolean) => void;
  subscribeFinalTranscript: (listener: (text: string) => void) => () => void;
  startNativeVoice: (lang: string) => void;
  stopNativeVoice: () => void;
  resolveVoiceLanguage: () => string;
}

const AiCopilotContext = createContext<AiCopilotContextValue | null>(null);

export const AiCopilotProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [state, dispatch] = useReducer(copilotReducer, INITIAL_STATE, (init) => ({
    ...init,
    voiceSettings: loadVoiceSettings(),
  }));
  const finalTranscriptListenersRef = useRef<Set<(text: string) => void>>(new Set());

  useEffect(() => {
    applyThemeToDocument(state.theme);
  }, [state.theme]);

  useEffect(() => {
    if (typeof document !== 'undefined') {
      document.documentElement.lang = state.locale;
    }
  }, [state.locale]);

  useEffect(() => {
    const unsub = bridgeClient.subscribe((message) => {
      dispatch({ type: 'HOST_MESSAGE', message });
      if (message.type === 'voice.transcript' && message.payload.isFinal) {
        const text = message.payload.text?.trim();
        if (text) {
          finalTranscriptListenersRef.current.forEach((listener) => listener(text));
        }
      }
    });
    bridgeClient.post({ type: 'ui.ready' });
    return unsub;
  }, []);

  const tr = useCallback(
    (key: string, ...args: Array<string | number>) =>
      translate(state.strings, state.locale, key, ...args),
    [state.strings, state.locale]
  );

  const sendPrompt = useCallback((prompt: string) => {
    const trimmed = prompt.trim();
    if (!trimmed) return;
    dispatch({ type: 'PUSH_PROMPT_HISTORY', prompt: trimmed });
    bridgeClient.post({ type: 'chat.send', payload: { prompt: trimmed } });
  }, []);

  const stopGenerating = useCallback(() => {
    bridgeClient.post({ type: 'chat.stop' });
  }, []);

  const newSession = useCallback(() => {
    bridgeClient.post({ type: 'chat.newSession' });
  }, []);

  const closePanel = useCallback(() => {
    bridgeClient.post({ type: 'panel.close' });
  }, []);

  const seekTimeline = useCallback((seconds: number) => {
    bridgeClient.post({ type: 'timeline.seek', payload: { seconds } });
  }, []);

  const acceptChanges = useCallback((turnId: string) => {
    bridgeClient.post({ type: 'changes.accept', payload: { turnId } });
  }, []);

  const rejectChanges = useCallback((turnId: string) => {
    bridgeClient.post({ type: 'changes.reject', payload: { turnId } });
  }, []);

  const undoChanges = useCallback((turnId: string) => {
    bridgeClient.post({ type: 'changes.undo', payload: { turnId } });
  }, []);

  const selectProvider = useCallback((provider: AiProviderKey, modelOverride?: string) => {
    bridgeClient.post({
      type: 'settings.selectProvider',
      payload: modelOverride ? { provider, modelOverride } : { provider },
    });
  }, []);

  const updateConfig = useCallback(
    (config: {
      modelName?: string;
      baseUrl?: string;
      newApiKey?: string;
      clearApiKey?: boolean;
    }) => {
      bridgeClient.post({ type: 'settings.updateConfig', payload: config });
    },
    []
  );

  const toggleAutoApply = useCallback((autoApply: boolean) => {
    bridgeClient.post({ type: 'settings.toggleAutoApply', payload: { autoApply } });
  }, []);

  const startSidecar = useCallback(() => {
    dispatch({ type: 'SET_SIDECAR_STARTING', starting: true });
    bridgeClient.post({ type: 'sidecar.start' });
  }, []);

  const refreshEnv = useCallback(() => {
    bridgeClient.post({ type: 'env.refresh' });
  }, []);

  const setSettingsOpen = useCallback(
    (open: boolean) => {
      dispatch({ type: 'SET_SETTINGS_OPEN', open });
      if (open) {
        bridgeClient.post({ type: 'env.refresh' });
      }
    },
    []
  );

  const updateVoiceSettings = useCallback((partial: Partial<VoiceSettings>) => {
    dispatch({ type: 'UPDATE_VOICE_SETTINGS', partial });
  }, []);

  const setVoiceState = useCallback((partial: Partial<VoiceState>) => {
    dispatch({ type: 'SET_VOICE_STATE', partial });
  }, []);

  const emitVoiceTranscript = useCallback((text: string, isFinal: boolean) => {
    dispatch({
      type: 'HOST_MESSAGE',
      message: {
        type: 'voice.transcript',
        payload: { text, isFinal },
      },
    });
    if (isFinal) {
      const trimmed = text.trim();
      if (trimmed) {
        finalTranscriptListenersRef.current.forEach((listener) => listener(trimmed));
      }
    }
  }, []);

  const subscribeFinalTranscript = useCallback((listener: (text: string) => void) => {
    finalTranscriptListenersRef.current.add(listener);
    return () => {
      finalTranscriptListenersRef.current.delete(listener);
    };
  }, []);

  const startNativeVoice = useCallback((lang: string) => {
    dispatch({
      type: 'SET_VOICE_STATE',
      partial: { isListening: true, interimText: '', error: null, fallbackToWebSpeech: false },
    });
    bridgeClient.post({ type: 'voice.start', payload: { lang } });
  }, []);

  const stopNativeVoice = useCallback(() => {
    dispatch({
      type: 'SET_VOICE_STATE',
      partial: { isListening: false, interimText: '' },
    });
    bridgeClient.post({ type: 'voice.stop' });
  }, []);

  const resolveVoiceLanguage = useCallback(() => {
    if (state.voiceSettings.language !== 'auto') {
      return state.voiceSettings.language;
    }
    return state.locale === 'en' ? 'en-US' : 'zh-CN';
  }, [state.voiceSettings.language, state.locale]);

  return (
    <AiCopilotContext.Provider
      value={{
        state,
        tr,
        sendPrompt,
        stopGenerating,
        newSession,
        closePanel,
        seekTimeline,
        acceptChanges,
        rejectChanges,
        undoChanges,
        selectProvider,
        updateConfig,
        toggleAutoApply,
        startSidecar,
        refreshEnv,
        setSettingsOpen,
        updateVoiceSettings,
        setVoiceState,
        emitVoiceTranscript,
        subscribeFinalTranscript,
        startNativeVoice,
        stopNativeVoice,
        resolveVoiceLanguage,
      }}
    >
      {children}
    </AiCopilotContext.Provider>
  );
};

export function useAiCopilotStore(): AiCopilotContextValue {
  const ctx = useContext(AiCopilotContext);
  if (!ctx) {
    throw new Error('useAiCopilotStore must be used inside AiCopilotProvider');
  }
  return ctx;
}
