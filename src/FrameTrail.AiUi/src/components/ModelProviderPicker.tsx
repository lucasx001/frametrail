import React, { useState } from 'react';
import { Check, ChevronDown, Cpu } from 'lucide-react';
import type { AiProviderKey } from '@/bridge/protocol';
import {
  ModelSelector,
  ModelSelectorContent,
  ModelSelectorEmpty,
  ModelSelectorGroup,
  ModelSelectorInput,
  ModelSelectorItem,
  ModelSelectorList,
  ModelSelectorSeparator,
  ModelSelectorTrigger,
} from '@/components/ai-elements/model-selector';
import { Button } from '@/components/ui/button';
import {
  findProviderModelPreset,
  formatProviderPillLabel,
  getProviderModelPresets,
  isCliDefaultModel,
  isCliProvider,
  type ProviderModelPreset,
} from '@/lib/providerModels';
import { cn } from '@/lib/utils';
import { useAiCopilotStore } from '@/store/useAiCopilotStore';

interface ModelOption {
  id: string;
  label: string;
  provider: AiProviderKey;
  modelOverride?: string;
  ready: boolean;
  isSubModel?: boolean;
  isDefault?: boolean;
  testId?: string;
}

export const ModelProviderPicker: React.FC = () => {
  const { state, tr, selectProvider } = useAiCopilotStore();
  const [open, setOpen] = useState(false);
  const [subModelOpen, setSubModelOpen] = useState(false);

  const { settings, env } = state;

  const pillLabel = formatProviderPillLabel(
    settings.provider,
    settings.modelName,
    env.ollamaModels
  );

  const isCurrentReady = (() => {
    switch (settings.provider) {
      case 'antigravity_cli':
        return env.hasAntigravityCli;
      case 'claude_code_cli':
        return env.hasClaudeCodeCli;
      case 'codex_cli':
        return env.hasCodexCli;
      case 'ollama':
        return env.hasOllamaServer;
      default:
        return env.sidecarOnline && settings.hasApiKey;
    }
  })();

  const activePresets = getProviderModelPresets(settings.provider, env.ollamaModels);
  const activePreset = findProviderModelPreset(
    settings.provider,
    settings.modelName,
    env.ollamaModels
  );

  const getPresetDisplayLabel = (preset: ProviderModelPreset): string => {
    if (preset.isDefault && isCliProvider(settings.provider)) {
      return tr('AiCliDefaultModel');
    }
    return preset.label;
  };

  const subModelButtonLabel = (() => {
    if (activePreset) {
      return getPresetDisplayLabel(activePreset);
    }
    return settings.modelName.trim() || tr('AiCliDefaultModel');
  })();

  const isPresetSelected = (preset: ProviderModelPreset): boolean => {
    const current = settings.modelName.trim().toLowerCase();
    if (preset.isDefault && isCliProvider(settings.provider) && isCliDefaultModel(current)) {
      return true;
    }
    return current === preset.id.toLowerCase();
  };

  const activeSubModelOptions: ModelOption[] = activePresets.map((preset) => ({
    id: `submodel:${settings.provider}:${preset.id}`,
    label: getPresetDisplayLabel(preset),
    provider: settings.provider,
    modelOverride: preset.id,
    ready: isCurrentReady,
    isSubModel: true,
    isDefault: preset.isDefault,
    testId: `provider-submodel-item-${preset.id}`,
  }));

  const localClis: ModelOption[] = [
    {
      id: 'antigravity_cli:agy-cli',
      label: 'Antigravity CLI (agy)',
      provider: 'antigravity_cli',
      modelOverride: 'agy-cli',
      ready: env.hasAntigravityCli,
    },
    {
      id: 'claude_code_cli:claude-code-cli',
      label: 'Claude Code CLI',
      provider: 'claude_code_cli',
      modelOverride: 'claude-code-cli',
      ready: env.hasClaudeCodeCli,
    },
    {
      id: 'codex_cli:codex-cli',
      label: 'OpenAI Codex CLI',
      provider: 'codex_cli',
      modelOverride: 'codex-cli',
      ready: env.hasCodexCli,
    },
  ];

  const localModels: ModelOption[] =
    env.hasOllamaServer && env.ollamaModels.length > 0
      ? env.ollamaModels.slice(0, 6).map((model) => ({
          id: `ollama:${model}`,
          label: `Ollama · ${model}`,
          provider: 'ollama',
          modelOverride: model,
          ready: true,
          isSubModel: true,
        }))
      : [
          {
            id: 'ollama:default',
            label: 'Ollama Local (localhost:11434)',
            provider: 'ollama',
            ready: env.hasOllamaServer,
          },
        ];

  const cloudApis: ModelOption[] = [
    {
      id: 'openai',
      label: 'OpenAI API (gpt-4o)',
      provider: 'openai',
      ready: Boolean(settings.providerHasApiKey['openai']),
    },
    {
      id: 'anthropic',
      label: 'Anthropic Claude API',
      provider: 'anthropic',
      ready: Boolean(settings.providerHasApiKey['anthropic']),
    },
    {
      id: 'deepseek',
      label: 'DeepSeek API',
      provider: 'deepseek',
      ready: Boolean(settings.providerHasApiKey['deepseek']),
    },
  ];

  const isSelected = (opt: ModelOption) => {
    if (opt.provider !== settings.provider) return false;
    if (opt.isSubModel) {
      const current = settings.modelName.trim().toLowerCase();
      if (opt.isDefault && isCliProvider(opt.provider) && isCliDefaultModel(current)) {
        return true;
      }
      return current === (opt.modelOverride ?? '').toLowerCase();
    }
    return true;
  };

  const handleChoose = (opt: ModelOption) => {
    selectProvider(opt.provider, opt.modelOverride);
    setOpen(false);
  };

  const handleChooseSubModel = (preset: ProviderModelPreset) => {
    selectProvider(settings.provider, preset.id);
    setSubModelOpen(false);
  };

  const renderGroup = (heading: string, options: ModelOption[]) => (
    <ModelSelectorGroup heading={heading}>
      {options.map((opt) => {
        const selected = isSelected(opt);
        return (
          <ModelSelectorItem
            key={opt.id}
            data-testid={opt.testId}
            value={`${opt.label} ${opt.provider} ${opt.modelOverride ?? ''}`}
            onSelect={() => handleChoose(opt)}
            className="flex cursor-pointer items-center justify-between gap-2 text-xs"
          >
            <div className="flex min-w-0 items-center gap-2">
              <span
                className={cn(
                  'size-1.5 shrink-0 rounded-full',
                  opt.ready ? 'bg-[var(--success)]' : 'bg-muted-foreground/45'
                )}
              />
              <span className="truncate">{opt.label}</span>
            </div>
            {selected && <Check className="size-3.5 shrink-0 text-foreground" />}
          </ModelSelectorItem>
        );
      })}
    </ModelSelectorGroup>
  );

  return (
    <>
      <ModelSelector open={open} onOpenChange={setOpen}>
        <ModelSelectorTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="xs"
            data-testid="model-picker-trigger"
            aria-label={tr('AiProviderLabel')}
            className="h-6.5 min-w-0 max-w-[min(165px,100%)] shrink gap-1.5 rounded-lg bg-[var(--well)] px-2 text-[11px] font-normal text-foreground hover:bg-[var(--hover-well)]"
          >
            <span
              className={cn(
                'size-1.5 shrink-0 rounded-full',
                isCurrentReady ? 'bg-[var(--success)]' : 'bg-muted-foreground/50'
              )}
            />
            <span className="truncate">{pillLabel}</span>
            <ChevronDown className="size-3 shrink-0 text-muted-foreground" />
          </Button>
        </ModelSelectorTrigger>

        <ModelSelectorContent title={tr('AiProviderLabel')} className="max-w-[320px]">
          <ModelSelectorInput placeholder={tr('AiSearchModels')} className="text-xs" />
          <ModelSelectorList>
            <ModelSelectorEmpty className="py-4 text-center text-xs text-muted-foreground">
              {tr('AiNoModelsFound')}
            </ModelSelectorEmpty>
            {activeSubModelOptions.length > 0 && (
              <>
                {renderGroup(tr('AiCliSubModelLabel'), activeSubModelOptions)}
                <ModelSelectorSeparator />
              </>
            )}
            {renderGroup(tr('AiGroupLocalClis'), localClis)}
            <ModelSelectorSeparator />
            {renderGroup(tr('AiGroupLocalModels'), localModels)}
            <ModelSelectorSeparator />
            {renderGroup(tr('AiGroupCloudApis'), cloudApis)}
          </ModelSelectorList>
        </ModelSelectorContent>
      </ModelSelector>

      {activePresets.length > 0 && (
        <ModelSelector open={subModelOpen} onOpenChange={setSubModelOpen}>
          <ModelSelectorTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="xs"
              data-testid="cli-submodel-picker-trigger"
              aria-label={tr('AiCliSubModelLabel')}
              className="h-6.5 min-w-0 max-w-[min(145px,100%)] shrink gap-1 rounded-lg bg-[var(--well)] px-2 text-[10px] font-normal text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground"
            >
              <Cpu className="size-3 shrink-0 text-muted-foreground" />
              <span className="truncate">{subModelButtonLabel}</span>
              <ChevronDown className="size-2.5 shrink-0 text-muted-foreground" />
            </Button>
          </ModelSelectorTrigger>

          <ModelSelectorContent title={tr('AiCliSubModelLabel')} className="max-w-[300px]">
            <ModelSelectorInput placeholder={tr('AiSearchModels')} className="text-xs" />
            <ModelSelectorList>
              <ModelSelectorEmpty className="py-4 text-center text-xs text-muted-foreground">
                {tr('AiNoModelsFound')}
              </ModelSelectorEmpty>
              <ModelSelectorGroup heading={tr('AiCliSubModelLabel')}>
                {activePresets.map((preset) => {
                  const selected = isPresetSelected(preset);
                  const displayLabel = getPresetDisplayLabel(preset);
                  return (
                    <ModelSelectorItem
                      key={preset.id}
                      data-testid={`submodel-option-${preset.id}`}
                      value={`${displayLabel} ${preset.label} ${preset.id}`}
                      onSelect={() => handleChooseSubModel(preset)}
                      className="flex cursor-pointer items-center justify-between gap-2 text-xs"
                    >
                      <div className="flex min-w-0 flex-col">
                        <span className="truncate">{displayLabel}</span>
                        {displayLabel !== preset.id && (
                          <span className="truncate font-mono text-[10px] text-muted-foreground">
                            {preset.id}
                          </span>
                        )}
                      </div>
                      {selected && <Check className="size-3.5 shrink-0 text-foreground" />}
                    </ModelSelectorItem>
                  );
                })}
              </ModelSelectorGroup>
            </ModelSelectorList>
          </ModelSelectorContent>
        </ModelSelector>
      )}
    </>
  );
};
