import React, { useEffect, useState } from 'react';
import {
  Check,
  Keyboard,
  KeyRound,
  Mic,
  Play,
  RefreshCw,
  ShieldCheck,
  Trash2,
} from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Separator } from '@/components/ui/separator';
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet';
import {
  getProviderModelPresets,
  isCliDefaultModel,
  isCliProvider,
  type ProviderModelPreset,
} from '@/lib/providerModels';
import { cn } from '@/lib/utils';
import {
  useAiCopilotStore,
  type VoiceActivationMode,
  type VoiceEngineOption,
  type VoiceLanguageOption,
} from '@/store/useAiCopilotStore';

const VOICE_LANGUAGES: Array<{ value: VoiceLanguageOption; label?: string; labelKey?: string }> = [
  { value: 'auto', labelKey: 'AiVoiceLangAuto' },
  { value: 'zh-CN', label: '中文 (zh-CN)' },
  { value: 'en-US', label: 'English (en-US)' },
  { value: 'ja-JP', label: '日本語 (ja-JP)' },
];

export const SettingsDrawer: React.FC = () => {
  const {
    state,
    tr,
    setSettingsOpen,
    updateConfig,
    startSidecar,
    refreshEnv,
    updateVoiceSettings,
  } = useAiCopilotStore();

  const { settings, env, settingsOpen, voiceSettings } = state;

  const [modelName, setModelName] = useState(settings.modelName);
  const [baseUrl, setBaseUrl] = useState(settings.baseUrl);
  const [newApiKey, setNewApiKey] = useState('');

  useEffect(() => {
    setModelName(settings.modelName);
  }, [settings.modelName, settings.provider]);

  useEffect(() => {
    setBaseUrl(settings.baseUrl);
  }, [settings.baseUrl, settings.provider]);

  const detectedTools: string[] = [];
  if (env.hasAntigravityCli) detectedTools.push('agy');
  if (env.hasClaudeCodeCli) detectedTools.push('claude');
  if (env.hasCodexCli) detectedTools.push('codex');
  if (env.hasOllamaServer) detectedTools.push(`Ollama (${env.ollamaModels.length})`);

  const cliSummary =
    detectedTools.length > 0
      ? tr('AiLocalCliDetected', detectedTools.join(' · '))
      : tr('AiNoLocalCli');

  const commitModelAndBaseUrl = () => {
    const trimmedModel = modelName.trim();
    const trimmedBaseUrl = baseUrl.trim();
    if (trimmedModel !== settings.modelName || trimmedBaseUrl !== settings.baseUrl) {
      updateConfig({
        modelName: trimmedModel,
        baseUrl: trimmedBaseUrl,
      });
    }
  };

  const commitNewApiKey = () => {
    const trimmed = newApiKey.trim();
    if (!trimmed) return;
    updateConfig({ newApiKey: trimmed });
    // Immediately wipe write-only input from React state
    setNewApiKey('');
  };

  const activePresets = getProviderModelPresets(settings.provider, env.ollamaModels);

  const isPresetActive = (preset: ProviderModelPreset): boolean => {
    const current = modelName.trim().toLowerCase();
    if (preset.isDefault && isCliProvider(settings.provider) && isCliDefaultModel(current)) {
      return true;
    }
    return current === preset.id.toLowerCase();
  };

  const handleSelectModelPreset = (preset: ProviderModelPreset) => {
    setModelName(preset.id);
    updateConfig({ modelName: preset.id });
  };

  const statusText = env.sidecarStarting
    ? tr('AiSidecarStarting')
    : env.sidecarOnline
      ? tr('AiStatusSidecarOnline')
      : tr('AiStatusOfflineFallback');

  return (
    <Sheet open={settingsOpen} onOpenChange={setSettingsOpen}>
      <SheetContent
        side="left"
        className="w-[90%] max-w-[340px] gap-0 border-border bg-background p-0 text-foreground"
      >
        <SheetHeader className="border-b border-border px-4 py-3">
          <SheetTitle className="text-[13px] font-semibold">
            {tr('AiCopilotSettings')}
          </SheetTitle>
          <SheetDescription className="text-[11px] text-muted-foreground">
            {tr('AiProviderLabel')}:{' '}
            <span className="font-mono text-foreground">{settings.provider}</span>
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
          {/* Sidecar & Environment Status */}
          <div className="flex flex-col gap-2.5 rounded-xl border border-border bg-card p-3">
            <div className="flex items-start justify-between gap-2">
              <div className="flex min-w-0 flex-col gap-1">
                <div className="flex items-center gap-1.5">
                  <span
                    className={cn(
                      'size-2 shrink-0 rounded-full',
                      env.sidecarOnline
                        ? 'bg-[var(--success)]'
                        : env.sidecarStarting
                          ? 'animate-pulse bg-amber-400'
                          : 'bg-amber-400'
                    )}
                  />
                  <span className="truncate text-[11px] font-semibold text-foreground">
                    {statusText}
                  </span>
                </div>
                <p className="text-[10px] leading-relaxed text-muted-foreground">
                  {cliSummary}
                </p>
              </div>
              <Button
                type="button"
                variant="ghost"
                size="icon-xs"
                aria-label="Refresh environment"
                onClick={refreshEnv}
                className="shrink-0 text-muted-foreground hover:text-foreground"
              >
                <RefreshCw className="size-3" />
              </Button>
            </div>

            {env.sidecarError && (
              <p className="rounded-md bg-destructive/10 px-2 py-1 text-[10px] text-destructive">
                {env.sidecarError}
              </p>
            )}

            {!env.sidecarOnline && (
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={env.sidecarStarting}
                onClick={startSidecar}
                className="h-7 w-full gap-1.5 rounded-lg text-[11px]"
              >
                <Play className="size-3" />
                {env.sidecarStarting ? tr('AiSidecarStarting') : tr('AiStartSidecar')}
              </Button>
            )}
          </div>

          <Separator />

          {/* Model Configuration */}
          <div className="flex flex-col gap-3">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="ai-settings-model" className="text-[11px] text-muted-foreground">
                {tr('AiModelLabel')}
              </Label>
              <Input
                id="ai-settings-model"
                value={modelName}
                onChange={(e) => setModelName(e.target.value)}
                onBlur={commitModelAndBaseUrl}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    commitModelAndBaseUrl();
                  }
                }}
                placeholder="gpt-4o"
                className="h-8 rounded-lg border-border bg-card text-xs"
              />

              {activePresets.length > 0 && (
                <div className="mt-1 flex flex-col gap-1.5">
                  <span className="text-[10px] text-muted-foreground">
                    {tr('AiModelPresetsLabel')}
                  </span>
                  <div
                    data-testid="settings-model-presets"
                    className="flex flex-wrap gap-1"
                  >
                    {activePresets.map((preset) => {
                      const active = isPresetActive(preset);
                      const displayLabel =
                        preset.isDefault && isCliProvider(settings.provider)
                          ? tr('AiCliDefaultModel')
                          : preset.label;
                      return (
                        <button
                          key={preset.id}
                          type="button"
                          data-testid={`settings-model-preset-${preset.id}`}
                          onClick={() => handleSelectModelPreset(preset)}
                          className={cn(
                            'inline-flex h-6 items-center gap-1 rounded-md border px-2 text-[10px] transition-colors',
                            active
                              ? 'border-foreground/30 bg-foreground text-background font-medium'
                              : 'border-border bg-card text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground'
                          )}
                        >
                          <span className="truncate">{displayLabel}</span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>

            {/* Write-Only API Key Input */}
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center justify-between gap-2">
                <Label htmlFor="ai-settings-apikey" className="text-[11px] text-muted-foreground">
                  {tr('AiApiKeyLabel')}
                </Label>
                {settings.hasApiKey && (
                  <div className="flex items-center gap-1">
                    <Badge
                      data-testid="api-key-saved-badge"
                      variant="secondary"
                      className="h-5 gap-1 rounded-md bg-[var(--success)]/15 px-1.5 text-[10px] font-medium text-[var(--success)]"
                    >
                      <ShieldCheck className="size-3" />
                      {tr('AiApiKeySavedHint')}
                    </Badge>
                    <Button
                      type="button"
                      variant="ghost"
                      size="xs"
                      data-testid="api-key-clear-btn"
                      onClick={() => updateConfig({ clearApiKey: true })}
                      className="h-5 gap-1 px-1.5 text-[10px] text-muted-foreground hover:text-destructive"
                    >
                      <Trash2 className="size-2.5" />
                      {tr('AiApiKeyClear')}
                    </Button>
                  </div>
                )}
              </div>

              <div className="flex items-center gap-1.5">
                <div className="relative flex-1">
                  <KeyRound className="pointer-events-none absolute top-1/2 left-2.5 size-3 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    id="ai-settings-apikey"
                    type="password"
                    autoComplete="new-password"
                    value={newApiKey}
                    onChange={(e) => setNewApiKey(e.target.value)}
                    onBlur={commitNewApiKey}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault();
                        commitNewApiKey();
                      }
                    }}
                    placeholder={
                      settings.hasApiKey
                        ? tr('AiApiKeyReplacePlaceholder')
                        : tr('AiApiKeyPlaceholder')
                    }
                    className="h-8 rounded-lg border-border bg-card pl-7 text-xs"
                  />
                </div>
                {newApiKey.trim().length > 0 && (
                  <Button
                    type="button"
                    size="icon-sm"
                    aria-label="Save API Key"
                    onClick={commitNewApiKey}
                    className="size-8 shrink-0 rounded-lg"
                  >
                    <Check className="size-3.5" />
                  </Button>
                )}
              </div>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="ai-settings-baseurl" className="text-[11px] text-muted-foreground">
                {tr('AiBaseUrlLabel')}
              </Label>
              <Input
                id="ai-settings-baseurl"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
                onBlur={commitModelAndBaseUrl}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    commitModelAndBaseUrl();
                  }
                }}
                placeholder={settings.baseUrlPlaceholder || 'https://api.openai.com/v1'}
                className="h-8 rounded-lg border-border bg-card text-xs"
              />
            </div>
          </div>

          <Separator />

          {/* Antigravity-style Voice Input Settings */}
          <div
            data-testid="voice-settings-section"
            className="flex flex-col gap-3 rounded-xl border border-border bg-card p-3"
          >
            <div className="flex items-center justify-between gap-2">
              <div className="flex items-center gap-1.5">
                <Mic className="size-3.5 text-foreground" />
                <span className="text-[11px] font-semibold text-foreground">
                  {tr('AiVoiceSettingsTitle')}
                </span>
              </div>
              <Badge
                data-testid="voice-shortcut-hint"
                variant="secondary"
                className="h-5 gap-1 rounded-md bg-[var(--well)] px-1.5 font-mono text-[10px] font-normal text-muted-foreground"
              >
                <Keyboard className="size-2.5" />
                {tr('AiVoiceShortcutHint')}
              </Badge>
            </div>

            {/* Activation Mode */}
            <div className="flex flex-col gap-1.5">
              <Label className="text-[11px] text-muted-foreground">
                {tr('AiVoiceModeLabel')}
              </Label>
              <div className="grid grid-cols-2 gap-1.5">
                {(['toggle', 'hold'] as VoiceActivationMode[]).map((mode) => {
                  const active = voiceSettings.mode === mode;
                  const label =
                    mode === 'toggle' ? tr('AiVoiceModeToggle') : tr('AiVoiceModeHold');
                  return (
                    <button
                      key={mode}
                      type="button"
                      data-testid={`voice-mode-${mode}`}
                      aria-pressed={active}
                      onClick={() => updateVoiceSettings({ mode })}
                      className={cn(
                        'flex h-7 items-center justify-center rounded-lg border px-2 text-[10px] transition-colors',
                        active
                          ? 'border-foreground/30 bg-foreground text-background font-medium'
                          : 'border-border bg-background text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground'
                      )}
                    >
                      <span className="truncate">{label}</span>
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Recognition Language */}
            <div className="flex flex-col gap-1.5">
              <Label className="text-[11px] text-muted-foreground">
                {tr('AiVoiceLangLabel')}
              </Label>
              <div className="grid grid-cols-2 gap-1.5">
                {VOICE_LANGUAGES.map((item) => {
                  const active = voiceSettings.language === item.value;
                  const label = item.labelKey ? tr(item.labelKey) : item.label;
                  return (
                    <button
                      key={item.value}
                      type="button"
                      data-testid={`voice-lang-${item.value}`}
                      aria-pressed={active}
                      onClick={() => updateVoiceSettings({ language: item.value })}
                      className={cn(
                        'flex h-7 items-center justify-center rounded-lg border px-2 text-[10px] transition-colors',
                        active
                          ? 'border-foreground/30 bg-foreground text-background font-medium'
                          : 'border-border bg-background text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground'
                      )}
                    >
                      <span className="truncate">{label}</span>
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Speech Engine */}
            <div className="flex flex-col gap-1.5">
              <Label className="text-[11px] text-muted-foreground">
                {tr('AiVoiceEngineLabel')}
              </Label>
              <div className="flex flex-col gap-1">
                {(['native', 'web'] as VoiceEngineOption[]).map((engine) => {
                  const active = voiceSettings.engine === engine;
                  const label =
                    engine === 'native'
                      ? tr('AiVoiceEngineNative')
                      : tr('AiVoiceEngineWeb');
                  return (
                    <button
                      key={engine}
                      type="button"
                      data-testid={`voice-engine-${engine}`}
                      aria-pressed={active}
                      onClick={() => updateVoiceSettings({ engine })}
                      className={cn(
                        'flex h-7 items-center justify-between rounded-lg border px-2.5 text-[10px] transition-colors',
                        active
                          ? 'border-foreground/30 bg-foreground text-background font-medium'
                          : 'border-border bg-background text-muted-foreground hover:bg-[var(--hover-well)] hover:text-foreground'
                      )}
                    >
                      <span className="truncate">{label}</span>
                      {active && <Check className="size-3 shrink-0" />}
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Auto-send on finish */}
            <div className="flex items-center justify-between gap-2 pt-0.5">
              <Label className="text-[11px] text-muted-foreground">
                {tr('AiVoiceAutoSubmitLabel')}
              </Label>
              <button
                type="button"
                data-testid="voice-autosubmit-toggle"
                aria-pressed={voiceSettings.autoSubmit}
                onClick={() =>
                  updateVoiceSettings({ autoSubmit: !voiceSettings.autoSubmit })
                }
                className={cn(
                  'inline-flex h-5 w-9 shrink-0 items-center rounded-full border border-border p-0.5 transition-colors',
                  voiceSettings.autoSubmit ? 'bg-foreground' : 'bg-[var(--well)]'
                )}
              >
                <span
                  className={cn(
                    'size-3.5 rounded-full transition-transform',
                    voiceSettings.autoSubmit
                      ? 'translate-x-4 bg-background'
                      : 'translate-x-0 bg-muted-foreground'
                  )}
                />
              </button>
            </div>
          </div>
        </div>
      </SheetContent>
    </Sheet>
  );
};
