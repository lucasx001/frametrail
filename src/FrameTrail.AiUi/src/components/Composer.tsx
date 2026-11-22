import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowUp, Mic, Square } from 'lucide-react';
import {
  PromptInput,
  PromptInputBody,
  PromptInputFooter,
  PromptInputTextarea,
} from '@/components/ai-elements/prompt-input';
import { SpeechInput } from '@/components/ai-elements/speech-input';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import {
  SLASH_COMMANDS,
  expandSlashCommandTemplate,
  type SlashCommandItem,
} from '@/lib/slashCommands';
import { cn } from '@/lib/utils';
import { useAiCopilotStore } from '@/store/useAiCopilotStore';
import { ModelProviderPicker } from './ModelProviderPicker';
import { SlashCommandPalette } from './SlashCommandPalette';

export const Composer: React.FC = () => {
  const {
    state,
    tr,
    sendPrompt,
    stopGenerating,
    toggleAutoApply,
    subscribeFinalTranscript,
  } = useAiCopilotStore();

  const {
    context,
    settings,
    isSending,
    locale,
    promptHistory,
    composerFocusNonce,
    voiceSettings,
    voiceState,
  } = state;

  const [prompt, setPrompt] = useState('');
  const [slashDismissed, setSlashDismissed] = useState(false);
  const [slashActiveIndex, setSlashActiveIndex] = useState(0);
  const [historyIndex, setHistoryIndex] = useState(-1);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const wasListeningRef = useRef(false);
  const hadVoiceTranscriptRef = useRef(false);

  useEffect(() => {
    if (composerFocusNonce > 0) {
      textareaRef.current?.focus();
    }
  }, [composerFocusNonce]);

  // Append final voice transcripts cleanly to prompt
  useEffect(() => {
    return subscribeFinalTranscript((chunk) => {
      const trimmedChunk = chunk.trim();
      if (!trimmedChunk) return;
      hadVoiceTranscriptRef.current = true;
      setPrompt((prev) => {
        if (!prev) return trimmedChunk;
        const needsSpace =
          !prev.endsWith(' ') &&
          !prev.endsWith('\n') &&
          /^[a-zA-Z0-9]/.test(trimmedChunk);
        return `${prev}${needsSpace ? ' ' : ''}${trimmedChunk}`;
      });
      setSlashDismissed(false);
    });
  }, [subscribeFinalTranscript]);

  // Auto-submit when voice recording finishes if voiceSettings.autoSubmit is enabled
  useEffect(() => {
    if (voiceState.isListening) {
      wasListeningRef.current = true;
      return;
    }

    if (wasListeningRef.current) {
      wasListeningRef.current = false;
      if (
        voiceSettings.autoSubmit &&
        hadVoiceTranscriptRef.current &&
        !isSending &&
        prompt.trim().length > 0
      ) {
        hadVoiceTranscriptRef.current = false;
        sendPrompt(prompt.trim());
        setPrompt('');
        setSlashDismissed(false);
        setHistoryIndex(-1);
      } else {
        hadVoiceTranscriptRef.current = false;
      }
    }
  }, [
    voiceState.isListening,
    voiceSettings.autoSubmit,
    isSending,
    prompt,
    sendPrompt,
  ]);

  const slashQuery = useMemo(() => {
    if (!prompt.startsWith('/') || prompt.includes('\n')) return null;
    return prompt.trim().toLowerCase();
  }, [prompt]);

  const filteredSlashCommands = useMemo(() => {
    if (slashQuery === null) return [];
    if (slashQuery === '/') return SLASH_COMMANDS;
    return SLASH_COMMANDS.filter((item) =>
      item.command.toLowerCase().startsWith(slashQuery)
    );
  }, [slashQuery]);

  const slashOpen = slashQuery !== null && !slashDismissed;

  useEffect(() => {
    setSlashActiveIndex(0);
  }, [slashQuery]);

  const applySlashCommand = useCallback(
    (item: SlashCommandItem) => {
      const expanded = expandSlashCommandTemplate(
        item,
        locale,
        context.playheadSec,
        context.durationSec
      );
      setPrompt(expanded);
      setSlashDismissed(true);
      requestAnimationFrame(() => {
        const el = textareaRef.current;
        if (el) {
          el.focus();
          el.setSelectionRange(expanded.length, expanded.length);
        }
      });
    },
    [locale, context.playheadSec, context.durationSec]
  );

  const handleInsertPlayhead = () => {
    const sec = Math.round(context.playheadSec * 100) / 100;
    const stamp = `@${Number(sec.toFixed(2))}s `;
    const next = prompt + stamp;
    setPrompt(next);
    setSlashDismissed(false);
    requestAnimationFrame(() => {
      const el = textareaRef.current;
      if (el) {
        el.focus();
        el.setSelectionRange(next.length, next.length);
      }
    });
  };

  const handleSubmit = useCallback(
    ({ text }: { text: string }) => {
      if (isSending) return;
      const trimmed = (text || prompt).trim();
      if (!trimmed) return;
      sendPrompt(trimmed);
      setPrompt('');
      setSlashDismissed(false);
      setHistoryIndex(-1);
    },
    [isSending, prompt, sendPrompt]
  );

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (slashOpen) {
      if (e.key === 'Escape') {
        e.preventDefault();
        setSlashDismissed(true);
        return;
      }
      if (e.key === 'ArrowDown' && filteredSlashCommands.length > 0) {
        e.preventDefault();
        setSlashActiveIndex((prev) => (prev + 1) % filteredSlashCommands.length);
        return;
      }
      if (e.key === 'ArrowUp' && filteredSlashCommands.length > 0) {
        e.preventDefault();
        setSlashActiveIndex((prev) =>
          prev <= 0 ? filteredSlashCommands.length - 1 : prev - 1
        );
        return;
      }
      if (e.key === 'Enter' && !e.shiftKey && filteredSlashCommands.length > 0) {
        e.preventDefault();
        const chosen = filteredSlashCommands[slashActiveIndex] ?? filteredSlashCommands[0];
        if (chosen) {
          applySlashCommand(chosen);
        }
        return;
      }
    }

    if (e.key === 'ArrowUp' && prompt.length === 0 && promptHistory.length > 0) {
      e.preventDefault();
      const nextIdx =
        historyIndex < 0 ? promptHistory.length - 1 : Math.max(0, historyIndex - 1);
      setHistoryIndex(nextIdx);
      const recalled = promptHistory[nextIdx];
      setPrompt(recalled);
      requestAnimationFrame(() => {
        textareaRef.current?.setSelectionRange(recalled.length, recalled.length);
      });
    }
  };

  const clipLabel =
    context.selectedClipIndex >= 0
      ? `Clip ${context.selectedClipIndex + 1}`
      : `${context.clipCount} clip(s)`;

  return (
    <div className="shrink-0">
      <SlashCommandPalette
        open={slashOpen}
        items={filteredSlashCommands}
        activeIndex={slashActiveIndex}
        locale={locale}
        emptyText={tr('AiSlashEmpty')}
        onActiveIndexChange={setSlashActiveIndex}
        onSelect={applySlashCommand}
      />

      <div className="px-3 pt-1 pb-3">
        <PromptInput
          onSubmit={handleSubmit}
          className="rounded-2xl border border-border bg-card shadow-none"
        >
          {/* Context Pills & Live Voice Status Row */}
          <div className="flex w-full flex-wrap items-center gap-1.5 px-2.5 pt-2">
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  data-testid="insert-playhead-chip"
                  onClick={handleInsertPlayhead}
                  className="inline-flex h-5.5 items-center rounded-md bg-[var(--well)] px-2 font-mono text-[10px] text-muted-foreground transition-colors hover:bg-[var(--hover-well)] hover:text-foreground"
                >
                  @{context.playheadLabel}
                </button>
              </TooltipTrigger>
              <TooltipContent side="top">
                <p className="text-xs">{tr('AiChipInsertTime')}</p>
              </TooltipContent>
            </Tooltip>

            <span className="inline-flex h-5.5 items-center rounded-md bg-[var(--well)] px-2 text-[10px] text-muted-foreground">
              {clipLabel}
            </span>

            {voiceState.isListening && (
              <span
                data-testid="voice-listening-badge"
                className="inline-flex h-5.5 max-w-[220px] items-center gap-1.5 rounded-md bg-destructive/15 px-2 text-[10px] font-medium text-destructive"
              >
                <span className="size-1.5 shrink-0 animate-pulse rounded-full bg-destructive" />
                <Mic className="size-2.5 shrink-0" />
                <span className="truncate">
                  {voiceState.interimText || tr('AiVoiceListening')}
                </span>
              </span>
            )}

            {!voiceState.isListening && voiceState.error && (
              <span
                data-testid="voice-error-badge"
                className="inline-flex h-5.5 max-w-[220px] items-center rounded-md bg-destructive/10 px-2 text-[10px] text-destructive"
              >
                <span className="truncate">{voiceState.error}</span>
              </span>
            )}
          </div>

          <PromptInputBody>
            <PromptInputTextarea
              ref={textareaRef}
              value={prompt}
              onChange={(e) => {
                setPrompt(e.target.value);
                setSlashDismissed(false);
              }}
              onKeyDown={handleKeyDown}
              placeholder={tr('AiComposerPlaceholder')}
              className="min-h-10 max-h-32 resize-none border-0 bg-transparent px-2.5 py-1.5 text-xs shadow-none focus-visible:ring-0"
            />
          </PromptInputBody>

          <PromptInputFooter className="min-w-0 flex-wrap justify-start gap-1 px-2 pt-0.5 pb-2">
            <ModelProviderPicker />

            <Button
              type="button"
              variant="ghost"
              size="xs"
              data-testid="mode-toggle-btn"
              onClick={() => toggleAutoApply(!settings.autoApply)}
              className="h-6.5 min-w-0 max-w-full shrink rounded-lg bg-[var(--well)] px-2 text-[10px] font-normal text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground"
            >
              <span className="truncate">
                {settings.autoApply ? tr('AiModeAuto') : tr('AiModeReview')}
              </span>
            </Button>

            <div className="ml-auto flex shrink-0 items-center gap-1.5">
              <SpeechInput disabled={isSending} />

              {isSending ? (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      type="button"
                      size="icon-sm"
                      variant="destructive"
                      data-testid="stop-generating-btn"
                      aria-label={tr('AiStopGenerating')}
                      onClick={stopGenerating}
                      className="size-7 rounded-full"
                    >
                      <Square className="size-3 fill-current" />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent side="top">
                    <p className="text-xs">{tr('AiStopGenerating')}</p>
                  </TooltipContent>
                </Tooltip>
              ) : (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      type="submit"
                      size="icon-sm"
                      variant="default"
                      data-testid="send-prompt-btn"
                      aria-label={tr('AiSend')}
                      disabled={prompt.trim().length === 0}
                      className={cn(
                        'size-7 rounded-full',
                        prompt.trim().length === 0 && 'opacity-40'
                      )}
                    >
                      <ArrowUp className="size-3.5" />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent side="top">
                    <p className="text-xs">{tr('AiSend')}</p>
                  </TooltipContent>
                </Tooltip>
              )}
            </div>
          </PromptInputFooter>
        </PromptInput>
      </div>
    </div>
  );
};
