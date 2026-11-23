import React, { useMemo, useState } from 'react';
import { Check, Copy, RotateCw, Sparkles } from 'lucide-react';
import {
  Conversation,
  ConversationContent,
  ConversationEmptyState,
  ConversationScrollButton,
} from '@/components/ai-elements/conversation';
import {
  Message,
  MessageAction,
  MessageActions,
  MessageContent,
  MessageResponse,
} from '@/components/ai-elements/message';
import {
  Reasoning,
  ReasoningContent,
  ReasoningTrigger,
} from '@/components/ai-elements/reasoning';
import { Shimmer } from '@/components/ai-elements/shimmer';
import {
  Tool,
  ToolContent,
  ToolHeader,
  ToolOutput,
} from '@/components/ai-elements/tool';
import { extractSeekSeconds, linkifyTimestamps } from '@/lib/timestamps';
import { useAiCopilotStore, type CopilotTurn } from '@/store/useAiCopilotStore';
import { ActionApprovalCard } from './ActionApprovalCard';
import { TimelineChangesCard } from './TimelineChangesCard';

export const ChatThread: React.FC = () => {
  const { state, tr, sendPrompt, seekTimeline } = useAiCopilotStore();
  const { turns, context, locale } = state;

  const projectContextSummary = useMemo(() => {
    const pointerTag = context.hasPointer ? ' · Pointer' : '';
    return `${context.durationLabel} · ${context.clipCount} clip(s) · ${context.canvasWidth}×${context.canvasHeight}${pointerTag}`;
  }, [context]);

  const suggestions = useMemo(() => {
    const start = Math.round(context.playheadSec * 100) / 100;
    const end =
      context.durationSec > 0
        ? Math.round(Math.min(context.durationSec, start + 2) * 100) / 100
        : Math.round((start + 2) * 100) / 100;

    return [
      {
        id: 'polish',
        title: tr('AiSuggestSpeechPolishTitle'),
        desc: tr('AiSuggestSpeechPolishDesc'),
        prompt:
          locale === 'en'
            ? 'Polish voiceover by removing pauses, filler words, and stutters'
            : '一键优化配音表达，移除停顿、嗯啊哦语气词和口误重复',
      },
      {
        id: 'clicks',
        title: tr('AiSuggestClicksTitle'),
        desc: tr('AiSuggestClicksDesc'),
        prompt:
          locale === 'en'
            ? 'Add 1.8x zoom targeting recorded clicks'
            : '在关键点击处添加放大运镜',
      },
      {
        id: 'portrait',
        title: tr('AiSuggestPortraitTitle'),
        desc: tr('AiSuggestPortraitDesc'),
        prompt: locale === 'en' ? 'Switch canvas to 9:16 portrait' : '把画布改为 9:16 竖屏',
      },
      {
        id: 'focus',
        title: tr('AiSuggestFocusTitle'),
        desc: tr('AiSuggestFocusDesc'),
        prompt:
          locale === 'en'
            ? `Add focus spotlight blur from ${start}s to ${end}s`
            : `在 ${start}s 到 ${end}s 添加聚焦虚化`,
      },
    ];
  }, [context.playheadSec, context.durationSec, locale, tr]);

  if (turns.length === 0) {
    return (
      <div className="flex flex-1 flex-col overflow-y-auto px-4 py-6">
        <ConversationEmptyState className="h-auto flex-1 justify-center gap-4 p-0">
          <div className="flex flex-col items-center gap-2 text-center">
            <div className="flex size-10 items-center justify-center rounded-full bg-[var(--accent-soft)] text-foreground">
              <Sparkles className="size-4.5" />
            </div>
            <h2 className="text-base font-semibold text-foreground">
              {tr('AiEmptyHeadline')}
            </h2>
            <p className="max-w-[290px] text-[11px] leading-relaxed text-muted-foreground">
              {tr('AiEmptySubheadline')}
            </p>
            <div
              data-testid="project-context-pill"
              className="mt-1 rounded-lg bg-[var(--well)] px-2.5 py-1 font-mono text-[10px] text-muted-foreground"
            >
              {projectContextSummary}
            </div>
          </div>

          <div className="mt-2 grid w-full grid-cols-2 gap-2 text-left">
            {suggestions.map((item) => (
              <button
                key={item.id}
                type="button"
                onClick={() => sendPrompt(item.prompt)}
                className="flex flex-col items-start gap-1 rounded-xl border border-border bg-card p-2.5 text-left transition-colors hover:bg-accent"
              >
                <span className="text-xs font-semibold text-foreground">{item.title}</span>
                <span className="text-[10px] leading-snug text-muted-foreground">
                  {item.desc}
                </span>
              </button>
            ))}
          </div>
        </ConversationEmptyState>
      </div>
    );
  }

  return (
    <Conversation className="flex-1">
      <ConversationContent className="gap-4 px-3.5 py-3.5">
        {turns.map((turn) => (
          <TurnThreadItem
            key={turn.id}
            turn={turn}
            onSeek={seekTimeline}
            onRetry={() => sendPrompt(turn.userPrompt)}
            tr={tr}
          />
        ))}
      </ConversationContent>
      <ConversationScrollButton />
    </Conversation>
  );
};

interface TurnThreadItemProps {
  turn: CopilotTurn;
  onSeek: (seconds: number) => void;
  onRetry: () => void;
  tr: (key: string, ...args: Array<string | number>) => string;
}

const TurnThreadItem: React.FC<TurnThreadItemProps> = ({ turn, onSeek, onRetry, tr }) => {
  const [copied, setCopied] = useState(false);

  const isStreaming = turn.status === 'streaming';
  const markdownSource = turn.finalMarkdown || turn.streamMarkdown;
  const displayMarkdown = useMemo(() => linkifyTimestamps(markdownSource), [markdownSource]);

  const markdownComponents = useMemo(
    () => ({
      a: ({
        href,
        children,
        ...rest
      }: React.AnchorHTMLAttributes<HTMLAnchorElement>) => {
        const seekSec = extractSeekSeconds(href);
        if (seekSec !== null) {
          return (
            <button
              type="button"
              data-testid="seek-timestamp-btn"
              onClick={() => onSeek(seekSec)}
              className="inline-flex items-center rounded bg-[var(--well)] px-1 py-0.25 font-mono text-[11px] font-medium text-foreground underline decoration-dotted underline-offset-2 transition-colors hover:bg-[var(--hover-well)]"
            >
              {children}
            </button>
          );
        }
        return (
          <a href={href} target="_blank" rel="noreferrer" {...rest}>
            {children}
          </a>
        );
      },
    }),
    [onSeek]
  );

  const handleCopy = async () => {
    if (!markdownSource) return;
    try {
      await navigator.clipboard?.writeText(markdownSource);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Ignore clipboard failures in restricted environments
    }
  };

  const showReasoning =
    (isStreaming && !turn.thinkingDone) ||
    Boolean(turn.thinkingText) ||
    turn.elapsedSeconds !== undefined;

  return (
    <div className="flex flex-col gap-3">
      <Message from="user" className="max-w-full">
        <MessageContent className="rounded-xl border border-border bg-card px-3 py-2 text-[12.5px] leading-relaxed text-foreground">
          <span className="whitespace-pre-wrap break-words">{turn.userPrompt}</span>
        </MessageContent>
      </Message>

      <Message from="assistant" className="max-w-full">
        <MessageContent className="w-full gap-2 text-[12.5px] leading-relaxed text-foreground">
          {showReasoning && (
            <Reasoning
              isStreaming={isStreaming && !turn.thinkingDone}
              duration={
                turn.elapsedSeconds !== undefined
                  ? Math.max(0.1, Number(turn.elapsedSeconds.toFixed(1)))
                  : 0
              }
              className="mb-1"
            >
              <ReasoningTrigger
                className="text-[11px] text-muted-foreground"
                getThinkingMessage={(streaming) =>
                  streaming ? (
                    <Shimmer duration={1.2} className="text-[11px]">
                      {tr('AiThinkingLabel')}
                    </Shimmer>
                  ) : (
                    <span>
                      {tr('AiThoughtForSeconds', Math.max(0.1, turn.elapsedSeconds ?? 0.1))}
                    </span>
                  )
                }
              />
              {turn.thinkingText && (
                <ReasoningContent className="mt-1.5 border-l border-border pl-2.5 text-[11px] text-muted-foreground">
                  {turn.thinkingText}
                </ReasoningContent>
              )}
            </Reasoning>
          )}

          {turn.toolCalls.length > 0 && (
            <div className="flex flex-col gap-1.5">
              {turn.toolCalls.map((tc) => {
                const toolState = tc.running
                  ? 'input-available'
                  : tc.succeeded
                    ? 'output-available'
                    : 'output-error';
                return (
                  <Tool key={tc.toolName} className="mb-0 rounded-lg border-border bg-card">
                    <ToolHeader
                      type="dynamic-tool"
                      toolName={tc.toolName}
                      title={tc.toolName}
                      state={toolState}
                      className="px-2.5 py-1.5 text-xs"
                    />
                    {tc.summary && (
                      <ToolContent className="px-2.5 pt-0 pb-2 text-[11px]">
                        <ToolOutput
                          output={tc.succeeded ? tc.summary : undefined}
                          errorText={tc.succeeded ? undefined : tc.summary}
                        />
                      </ToolContent>
                    )}
                  </Tool>
                );
              })}
            </div>
          )}

          {turn.confirmations.length > 0 && (
            <div className="flex flex-col gap-2">
              {turn.confirmations.map((confirmation) => (
                <ActionApprovalCard key={confirmation.requestId} confirmation={confirmation} />
              ))}
            </div>
          )}

          {displayMarkdown && (
            <MessageResponse
              isAnimating={isStreaming}
              linkSafety={{ enabled: false }}
              components={markdownComponents}
              className="text-[12.5px] leading-relaxed text-foreground [&>p]:my-1 [&>ul]:my-1 [&>ul]:list-disc [&>ul]:pl-4"
            >
              {displayMarkdown}
            </MessageResponse>
          )}

          {turn.changesCard && <TimelineChangesCard changes={turn.changesCard} />}

          {!isStreaming && markdownSource && (
            <MessageActions className="mt-0.5 opacity-80 transition-opacity group-hover:opacity-100">
              <MessageAction
                tooltip={copied ? tr('AiCopied') : tr('AiCopyMessage')}
                label={copied ? tr('AiCopied') : tr('AiCopyMessage')}
                onClick={handleCopy}
                className="size-6 text-muted-foreground hover:text-foreground"
              >
                {copied ? <Check className="size-3" /> : <Copy className="size-3" />}
              </MessageAction>
              <MessageAction
                tooltip={tr('AiRetryMessage')}
                label={tr('AiRetryMessage')}
                onClick={onRetry}
                className="size-6 text-muted-foreground hover:text-foreground"
              >
                <RotateCw className="size-3" />
              </MessageAction>
            </MessageActions>
          )}
        </MessageContent>
      </Message>
    </div>
  );
};
