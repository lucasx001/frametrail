import React, { useState } from 'react';
import { AlertCircle, CheckCircle2, Download, Loader2, XCircle } from 'lucide-react';
import type { ActionConfirmationPayload } from '@/bridge/protocol';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { cn } from '@/lib/utils';
import { useAiCopilotStore } from '@/store/useAiCopilotStore';

interface ActionApprovalCardProps {
  confirmation: ActionConfirmationPayload;
}

export const ActionApprovalCard: React.FC<ActionApprovalCardProps> = ({ confirmation }) => {
  const { tr, respondToConfirmation } = useAiCopilotStore();
  const [submitting, setSubmitting] = useState(false);

  const statusText =
    confirmation.status === 'pending'
      ? tr('AiConfirmRequired')
      : confirmation.status === 'downloading'
        ? confirmation.progressPercent !== undefined
          ? `${tr('AiConfirmDownloading')} ${Math.round(confirmation.progressPercent)}%`
          : tr('AiConfirmDownloading')
        : confirmation.status === 'completed'
          ? tr('AiConfirmCompleted')
          : confirmation.status === 'rejected'
            ? tr('AiConfirmRejected')
            : tr('AiConfirmFailed');

  const statusBadgeClass =
    confirmation.status === 'pending'
      ? 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
      : confirmation.status === 'downloading'
        ? 'border-border bg-secondary text-foreground'
        : confirmation.status === 'completed'
          ? 'border-transparent bg-[var(--success)]/15 text-[var(--success)]'
          : confirmation.status === 'rejected'
            ? 'border-border bg-muted text-muted-foreground'
            : 'border-destructive/30 bg-destructive/10 text-destructive';

  const handleRespond = (approved: boolean) => {
    if (submitting || confirmation.status !== 'pending') return;
    setSubmitting(true);
    respondToConfirmation(confirmation.requestId, approved);
  };

  const progressPct = Math.max(0, Math.min(100, confirmation.progressPercent ?? 0));

  return (
    <Card
      data-testid="action-approval-card"
      data-request-id={confirmation.requestId}
      data-status={confirmation.status}
      className="gap-2 rounded-xl border-border bg-card p-3 shadow-none"
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex min-w-0 items-center gap-1.5">
          {confirmation.status === 'downloading' ? (
            <Loader2 className="size-3.5 shrink-0 animate-spin text-foreground" />
          ) : confirmation.status === 'completed' ? (
            <CheckCircle2 className="size-3.5 shrink-0 text-[var(--success)]" />
          ) : confirmation.status === 'rejected' ? (
            <XCircle className="size-3.5 shrink-0 text-muted-foreground" />
          ) : confirmation.status === 'failed' ? (
            <AlertCircle className="size-3.5 shrink-0 text-destructive" />
          ) : (
            <Download className="size-3.5 shrink-0 text-foreground" />
          )}
          <span className="truncate text-[11.5px] font-semibold text-foreground">
            {confirmation.title}
          </span>
        </div>

        <Badge
          variant="outline"
          data-testid="action-approval-status"
          className={cn('h-5 shrink-0 rounded-md px-1.5 text-[10px] font-semibold', statusBadgeClass)}
        >
          {statusText}
        </Badge>
      </div>

      <p className="text-[11.5px] leading-relaxed text-foreground/90">
        {confirmation.description}
      </p>

      {confirmation.detail && (
        <div className="truncate rounded-md bg-[var(--well)] px-2 py-1 font-mono text-[10px] text-muted-foreground" title={confirmation.detail}>
          {confirmation.detail}
        </div>
      )}

      {confirmation.status === 'downloading' && (
        <div className="flex flex-col gap-1">
          <div className="h-1.5 w-full overflow-hidden rounded-full bg-secondary">
            <div
              className="h-full rounded-full bg-foreground transition-[width] duration-200"
              style={{ width: `${progressPct}%` }}
            />
          </div>
          {confirmation.statusMessage && (
            <span className="text-[10.5px] text-muted-foreground">
              {confirmation.statusMessage}
            </span>
          )}
        </div>
      )}

      {confirmation.status !== 'downloading' && confirmation.status !== 'pending' && confirmation.statusMessage && (
        <div
          className={cn(
            'text-[10.5px]',
            confirmation.status === 'failed' ? 'text-destructive' : 'text-muted-foreground'
          )}
        >
          {confirmation.statusMessage}
        </div>
      )}

      {confirmation.status === 'pending' && (
        <div className="mt-0.5 flex items-center justify-end gap-1.5">
          <Button
            type="button"
            variant="secondary"
            size="xs"
            data-testid="action-approval-reject"
            disabled={submitting}
            className="h-6.5 rounded-lg px-2.5 text-[11px]"
            onClick={() => handleRespond(false)}
          >
            {confirmation.rejectLabel || tr('AiConfirmReject')}
          </Button>
          <Button
            type="button"
            variant="default"
            size="xs"
            data-testid="action-approval-approve"
            disabled={submitting}
            className="h-6.5 rounded-lg px-3 text-[11px] font-semibold"
            onClick={() => handleRespond(true)}
          >
            {confirmation.confirmLabel || tr('AiConfirmApproveDownload')}
          </Button>
        </div>
      )}
    </Card>
  );
};
