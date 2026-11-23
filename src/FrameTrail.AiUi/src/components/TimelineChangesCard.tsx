import React, { useState } from 'react';
import { RotateCcw } from 'lucide-react';
import type { TimelineChangesPayload } from '@/bridge/protocol';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { cn } from '@/lib/utils';
import { useAiCopilotStore } from '@/store/useAiCopilotStore';

interface TimelineChangesCardProps {
  changes: TimelineChangesPayload;
}

export const TimelineChangesCard: React.FC<TimelineChangesCardProps> = ({ changes }) => {
  const { tr, state, seekTimeline, acceptChanges, rejectChanges, undoChanges } =
    useAiCopilotStore();
  const [expanded, setExpanded] = useState(false);

  const statusText =
    changes.state === 'previewing'
      ? tr('AiStatusPreviewing')
      : changes.state === 'applied'
        ? tr('AiStatusApplied')
        : tr('AiStatusReverted');

  const statusBadgeClass =
    changes.state === 'previewing'
      ? 'border-border bg-secondary text-foreground'
      : changes.state === 'applied'
        ? 'border-transparent bg-[var(--success)]/15 text-[var(--success)]'
        : 'border-border bg-muted text-muted-foreground';

  const visibleItems = expanded ? changes.items : changes.items.slice(0, 10);
  const overflowCount = Math.max(0, changes.items.length - 10);

  return (
    <Card
      data-testid="timeline-changes-card"
      className="gap-2 rounded-xl border-border bg-card p-3 shadow-none"
    >
      <div className="flex items-center justify-between gap-2">
        <span className="text-[11px] font-semibold text-foreground">
          {changes.revision !== undefined
            ? `${tr('AiTimelineChanges')} · #${changes.revision}`
            : tr('AiTimelineChanges')}
        </span>
        <Badge
          variant="outline"
          data-testid="timeline-changes-status"
          className={cn('h-5 rounded-md px-1.5 text-[10px] font-semibold', statusBadgeClass)}
        >
          {statusText}
        </Badge>
      </div>

      <div className="flex flex-col gap-1">
        {visibleItems.map((item, idx) => {
          const badgeColor =
            item.badge === '+'
              ? 'text-[var(--success)]'
              : item.badge === '−' || item.badge === '-'
                ? 'text-destructive'
                : 'text-foreground';

          const description = item.detail ? `${item.label}  ${item.detail}` : item.label;

          return (
            <div
              key={`${item.label}-${item.timeLabel}-${idx}`}
              className="grid grid-cols-[16px_1fr_auto] items-center gap-1.5 text-[11px]"
            >
              <span className={cn('font-mono text-xs font-semibold leading-none', badgeColor)}>
                {item.badge}
              </span>
              <span className="truncate text-foreground" title={description}>
                {description}
              </span>
              {item.timeLabel ? (
                item.seekSeconds !== undefined ? (
                  <button
                    type="button"
                    onClick={() => seekTimeline(item.seekSeconds!)}
                    className="inline-flex h-5 items-center rounded-md bg-[var(--well)] px-1.5 font-mono text-[10px] text-foreground transition-colors hover:bg-[var(--hover-well)]"
                  >
                    {item.timeLabel}
                  </button>
                ) : (
                  <span className="font-mono text-[10px] text-muted-foreground">
                    {item.timeLabel}
                  </span>
                )
              ) : null}
            </div>
          );
        })}

        {overflowCount > 0 && (
          <button
            type="button"
            data-testid="timeline-changes-toggle-more"
            onClick={() => setExpanded((prev) => !prev)}
            className="mt-0.5 self-start text-[10px] font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            {expanded ? tr('AiShowLessEdits') : tr('AiMoreEdits', overflowCount)}
          </button>
        )}
      </div>

      {changes.state !== 'reverted' && (
        <div className="mt-0.5 flex items-center justify-end gap-1.5">
          {changes.state === 'previewing' ? (
            <>
              <Button
                type="button"
                variant="secondary"
                size="xs"
                disabled={state.isSending}
                className="h-6.5 rounded-lg px-2.5 text-[11px]"
                onClick={() => rejectChanges(changes.turnId)}
              >
                {tr('AiRejectChanges')}
              </Button>
              <Button
                type="button"
                variant="default"
                size="xs"
                disabled={state.isSending}
                className="h-6.5 rounded-lg px-3 text-[11px] font-semibold"
                onClick={() => acceptChanges(changes.turnId)}
              >
                {tr('AiAcceptChanges')}
              </Button>
            </>
          ) : (
            <Button
              type="button"
              variant="secondary"
              size="xs"
              disabled={state.isSending}
              className="h-6.5 gap-1 rounded-lg px-2.5 text-[11px]"
              onClick={() => undoChanges(changes.turnId)}
            >
              <RotateCcw className="size-3" />
              {tr('AiRevertChanges')}
            </Button>
          )}
        </div>
      )}
    </Card>
  );
};
