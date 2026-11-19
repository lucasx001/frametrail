import React, { useCallback, useEffect, useRef } from 'react';
import { Mic, MicOff } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';
import { useAiCopilotStore } from '@/store/useAiCopilotStore';

interface BrowserSpeechRecognitionAlternative {
  transcript: string;
}

interface BrowserSpeechRecognitionResult {
  isFinal: boolean;
  length: number;
  [index: number]: BrowserSpeechRecognitionAlternative;
}

interface BrowserSpeechRecognitionResultList {
  length: number;
  [index: number]: BrowserSpeechRecognitionResult;
}

interface BrowserSpeechRecognitionEvent {
  resultIndex: number;
  results: BrowserSpeechRecognitionResultList;
}

interface BrowserSpeechRecognitionErrorEvent {
  error: string;
  message?: string;
}

interface BrowserSpeechRecognitionInstance {
  continuous: boolean;
  interimResults: boolean;
  lang: string;
  onstart: (() => void) | null;
  onresult: ((event: BrowserSpeechRecognitionEvent) => void) | null;
  onerror: ((event: BrowserSpeechRecognitionErrorEvent) => void) | null;
  onend: (() => void) | null;
  start: () => void;
  stop: () => void;
  abort?: () => void;
}

type BrowserSpeechRecognitionConstructor = new () => BrowserSpeechRecognitionInstance;

declare global {
  interface Window {
    SpeechRecognition?: BrowserSpeechRecognitionConstructor;
    webkitSpeechRecognition?: BrowserSpeechRecognitionConstructor;
  }
}

export interface SpeechInputProps {
  className?: string;
  disabled?: boolean;
}

function getBrowserSpeechRecognitionCtor(): BrowserSpeechRecognitionConstructor | null {
  if (typeof window === 'undefined') return null;
  return window.SpeechRecognition ?? window.webkitSpeechRecognition ?? null;
}

export const SpeechInput: React.FC<SpeechInputProps> = ({ className, disabled }) => {
  const {
    state,
    tr,
    setVoiceState,
    emitVoiceTranscript,
    startNativeVoice,
    stopNativeVoice,
    resolveVoiceLanguage,
  } = useAiCopilotStore();

  const { voiceSettings, voiceState } = state;
  const { isListening } = voiceState;

  const webRecognitionRef = useRef<BrowserSpeechRecognitionInstance | null>(null);
  const activeBackendRef = useRef<'native' | 'web' | null>(null);

  const stopWebSpeech = useCallback(() => {
    const rec = webRecognitionRef.current;
    webRecognitionRef.current = null;
    if (rec) {
      rec.onend = null;
      rec.onresult = null;
      rec.onerror = null;
      try {
        rec.stop();
      } catch {
        // Ignore stop errors if already stopped
      }
    }
  }, []);

  const startWebSpeech = useCallback(
    (lang: string) => {
      stopWebSpeech();
      const Ctor = getBrowserSpeechRecognitionCtor();
      if (!Ctor) {
        setVoiceState({
          isListening: false,
          interimText: '',
          error: tr('AiVoiceUnsupported'),
          fallbackToWebSpeech: false,
        });
        activeBackendRef.current = null;
        return;
      }

      try {
        const rec = new Ctor();
        rec.continuous = true;
        rec.interimResults = true;
        rec.lang = lang;

        rec.onstart = () => {
          setVoiceState({
            isListening: true,
            interimText: '',
            error: null,
            fallbackToWebSpeech: false,
          });
        };

        rec.onresult = (event: BrowserSpeechRecognitionEvent) => {
          let interim = '';
          for (let i = event.resultIndex; i < event.results.length; i += 1) {
            const res = event.results[i];
            const transcript = res[0]?.transcript ?? '';
            if (res.isFinal) {
              emitVoiceTranscript(transcript, true);
            } else {
              interim += transcript;
            }
          }
          if (interim) {
            emitVoiceTranscript(interim, false);
          }
        };

        rec.onerror = (event: BrowserSpeechRecognitionErrorEvent) => {
          setVoiceState({
            isListening: false,
            interimText: '',
            error: event.message || event.error || tr('AiVoiceUnsupported'),
            fallbackToWebSpeech: false,
          });
          activeBackendRef.current = null;
        };

        rec.onend = () => {
          setVoiceState({
            isListening: false,
            interimText: '',
          });
          activeBackendRef.current = null;
          webRecognitionRef.current = null;
        };

        webRecognitionRef.current = rec;
        activeBackendRef.current = 'web';
        setVoiceState({
          isListening: true,
          interimText: '',
          error: null,
          fallbackToWebSpeech: false,
        });
        rec.start();
      } catch {
        setVoiceState({
          isListening: false,
          interimText: '',
          error: tr('AiVoiceUnsupported'),
          fallbackToWebSpeech: false,
        });
        activeBackendRef.current = null;
      }
    },
    [stopWebSpeech, setVoiceState, tr, emitVoiceTranscript]
  );

  // Automatic fallback from Native Windows Speech to Web Speech API when host reports fallbackToWebSpeech: true
  useEffect(() => {
    if (voiceState.fallbackToWebSpeech && activeBackendRef.current === 'native') {
      const Ctor = getBrowserSpeechRecognitionCtor();
      if (Ctor) {
        const lang = resolveVoiceLanguage();
        startWebSpeech(lang);
      } else {
        setVoiceState({
          isListening: false,
          interimText: '',
          error: voiceState.error || tr('AiVoiceUnsupported'),
          fallbackToWebSpeech: false,
        });
        activeBackendRef.current = null;
      }
    }
  }, [
    voiceState.fallbackToWebSpeech,
    voiceState.error,
    resolveVoiceLanguage,
    startWebSpeech,
    setVoiceState,
    tr,
  ]);

  const startListening = useCallback(() => {
    if (disabled || isListening) return;
    const lang = resolveVoiceLanguage();

    if (voiceSettings.engine === 'web') {
      startWebSpeech(lang);
    } else {
      activeBackendRef.current = 'native';
      startNativeVoice(lang);
    }
  }, [
    disabled,
    isListening,
    resolveVoiceLanguage,
    voiceSettings.engine,
    startWebSpeech,
    startNativeVoice,
  ]);

  const stopListening = useCallback(() => {
    if (!isListening && activeBackendRef.current === null) return;
    const backend = activeBackendRef.current;
    activeBackendRef.current = null;

    if (backend === 'web' || webRecognitionRef.current) {
      stopWebSpeech();
      setVoiceState({ isListening: false, interimText: '' });
    } else {
      stopNativeVoice();
    }
  }, [isListening, stopWebSpeech, setVoiceState, stopNativeVoice]);

  const toggleListening = useCallback(() => {
    if (isListening) {
      stopListening();
    } else {
      startListening();
    }
  }, [isListening, startListening, stopListening]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      stopWebSpeech();
    };
  }, [stopWebSpeech]);

  // Global keyboard shortcut: Ctrl+M / Cmd+M toggles voice input anywhere in the panel
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (
        (event.ctrlKey || event.metaKey) &&
        !event.shiftKey &&
        !event.altKey &&
        event.key.toLowerCase() === 'm'
      ) {
        event.preventDefault();
        toggleListening();
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [toggleListening]);

  const handleClick = () => {
    if (voiceSettings.mode === 'toggle') {
      toggleListening();
    }
  };

  const handlePointerDown = (e: React.PointerEvent<HTMLButtonElement>) => {
    if (voiceSettings.mode === 'hold') {
      e.preventDefault();
      startListening();
    }
  };

  const handlePointerUpOrCancel = () => {
    if (voiceSettings.mode === 'hold' && isListening) {
      stopListening();
    }
  };

  const label = isListening ? tr('AiVoiceStop') : tr('AiVoiceInput');

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          type="button"
          size="icon-sm"
          variant={isListening ? 'destructive' : 'ghost'}
          disabled={disabled}
          data-testid="voice-input-btn"
          data-listening={isListening ? 'true' : 'false'}
          data-mode={voiceSettings.mode}
          aria-label={label}
          aria-pressed={isListening}
          onClick={handleClick}
          onPointerDown={handlePointerDown}
          onPointerUp={handlePointerUpOrCancel}
          onPointerCancel={handlePointerUpOrCancel}
          onPointerLeave={handlePointerUpOrCancel}
          className={cn(
            'relative size-7 rounded-full transition-colors',
            isListening
              ? 'bg-destructive text-destructive-foreground shadow-sm'
              : 'bg-[var(--well)] text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground',
            className
          )}
        >
          {isListening && (
            <span
              data-testid="voice-listening-pulse"
              className="pointer-events-none absolute inset-0 animate-ping rounded-full bg-destructive/40"
            />
          )}
          {isListening ? (
            <MicOff className="relative size-3.5" />
          ) : (
            <Mic className="relative size-3.5" />
          )}
        </Button>
      </TooltipTrigger>
      <TooltipContent side="top">
        <p className="text-xs">{label}</p>
      </TooltipContent>
    </Tooltip>
  );
};

export default SpeechInput;
