import React from 'react';
import { PanelLeftClose, Plus, Settings, Sparkles } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';
import { useAiCopilotStore } from '@/store/useAiCopilotStore';

export const CopilotHeader: React.FC = () => {
  const { state, tr, newSession, closePanel, setSettingsOpen } = useAiCopilotStore();
  const { sidecarOnline, sidecarStarting } = state.env;

  const statusLabel = sidecarStarting
    ? tr('AiSidecarStarting')
    : sidecarOnline
      ? tr('AiStatusSidecarOnline')
      : tr('AiStatusOfflineFallback');

  return (
    <header className="flex h-11 shrink-0 items-center justify-between border-b border-border px-3.5 select-none">
      <div className="flex min-w-0 items-center gap-2">
        <Sparkles className="size-3.5 shrink-0 text-primary" />
        <h1 className="truncate text-[13px] font-semibold text-foreground">
          {tr('AiCopilotTitle')}
        </h1>
        <Tooltip>
          <TooltipTrigger asChild>
            <button
              type="button"
              aria-label={statusLabel}
              onClick={() => setSettingsOpen(true)}
              className="flex size-4 items-center justify-center rounded-full focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <span
                data-testid="sidecar-status-dot"
                className={cn(
                  'size-2 rounded-full transition-colors',
                  sidecarOnline
                    ? 'bg-[var(--success)]'
                    : sidecarStarting
                      ? 'animate-pulse bg-amber-400'
                      : 'bg-amber-400'
                )}
              />
            </button>
          </TooltipTrigger>
          <TooltipContent side="bottom">
            <p className="text-xs">{statusLabel}</p>
          </TooltipContent>
        </Tooltip>
      </div>

      <div className="flex items-center gap-0.5">
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="size-7 rounded-full text-muted-foreground hover:text-foreground"
              aria-label={tr('AiNewSession')}
              onClick={newSession}
            >
              <Plus className="size-3.5" />
            </Button>
          </TooltipTrigger>
          <TooltipContent side="bottom">
            <p className="text-xs">{tr('AiNewSession')}</p>
          </TooltipContent>
        </Tooltip>

        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="size-7 rounded-full text-muted-foreground hover:text-foreground"
              aria-label={tr('AiCopilotSettings')}
              onClick={() => setSettingsOpen(true)}
            >
              <Settings className="size-3.5" />
            </Button>
          </TooltipTrigger>
          <TooltipContent side="bottom">
            <p className="text-xs">{tr('AiCopilotSettings')}</p>
          </TooltipContent>
        </Tooltip>

        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="size-7 rounded-full text-muted-foreground hover:text-foreground"
              aria-label={tr('AiClosePanel')}
              onClick={closePanel}
            >
              <PanelLeftClose className="size-3.5" />
            </Button>
          </TooltipTrigger>
          <TooltipContent side="bottom">
            <p className="text-xs">{tr('AiClosePanel')}</p>
          </TooltipContent>
        </Tooltip>
      </div>
    </header>
  );
};
