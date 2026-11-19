import type { AiProviderKey } from '@/bridge/protocol';

export interface ProviderModelPreset {
  id: string;
  label: string;
  pillLabel: string;
  isDefault?: boolean;
}

export const PROVIDER_MODEL_PRESETS: Record<AiProviderKey, ProviderModelPreset[]> = {
  antigravity_cli: [
    { id: 'agy-cli', label: 'CLI Default (Auto)', pillLabel: 'agy CLI', isDefault: true },
    {
      id: 'gemini-4-argon-high',
      label: 'Gemini 4 Argon (High)',
      pillLabel: 'agy · Argon (High)',
    },
    {
      id: 'gemini-4-hydrogen-high',
      label: 'Gemini 4 Hydrogen (High)',
      pillLabel: 'agy · Hydrogen (High)',
    },
    {
      id: 'gemini-4-neon-high',
      label: 'Gemini 4 Neon (High)',
      pillLabel: 'agy · Neon (High)',
    },
    {
      id: 'gemini-3.8-flash-high',
      label: 'Gemini 3.8 Flash (High)',
      pillLabel: 'agy · 3.8 Flash',
    },
    {
      id: 'claude-sonnet-5-5-high',
      label: 'Claude Sonnet 5.5 (High)',
      pillLabel: 'agy · Sonnet 5.5',
    },
    {
      id: 'claude-opus-5-5-high',
      label: 'Claude Opus 5.5 (High)',
      pillLabel: 'agy · Opus 5.5',
    },
    {
      id: 'gpt-oss-120b-medium',
      label: 'GPT-OSS 120B (Medium)',
      pillLabel: 'agy · GPT-OSS 120B',
    },
  ],
  codex_cli: [
    { id: 'codex-cli', label: 'CLI Default (Auto)', pillLabel: 'Codex CLI', isDefault: true },
    { id: 'gpt-5.3-codex', label: 'GPT-5.3 Codex', pillLabel: 'Codex · GPT-5.3' },
    { id: 'gpt-5-codex', label: 'GPT-5 Codex', pillLabel: 'Codex · GPT-5' },
    { id: 'o3', label: 'o3', pillLabel: 'Codex · o3' },
    { id: 'o4-mini', label: 'o4-mini', pillLabel: 'Codex · o4-mini' },
    { id: 'codex-mini-latest', label: 'codex-mini-latest', pillLabel: 'Codex · Mini' },
    { id: 'gpt-4.1', label: 'GPT-4.1', pillLabel: 'Codex · GPT-4.1' },
  ],
  claude_code_cli: [
    {
      id: 'claude-code-cli',
      label: 'CLI Default (Auto)',
      pillLabel: 'Claude CLI',
      isDefault: true,
    },
    { id: 'sonnet', label: 'Claude Sonnet (Alias)', pillLabel: 'Claude · Sonnet' },
    { id: 'opus', label: 'Claude Opus (Alias)', pillLabel: 'Claude · Opus' },
    {
      id: 'claude-sonnet-4-5',
      label: 'Claude Sonnet 4.5',
      pillLabel: 'Claude · Sonnet 4.5',
    },
    {
      id: 'claude-opus-4-5',
      label: 'Claude Opus 4.5',
      pillLabel: 'Claude · Opus 4.5',
    },
  ],
  openai: [
    { id: 'gpt-4o', label: 'gpt-4o', pillLabel: 'gpt-4o', isDefault: true },
    { id: 'gpt-4.1', label: 'gpt-4.1', pillLabel: 'gpt-4.1' },
    { id: 'o4-mini', label: 'o4-mini', pillLabel: 'o4-mini' },
    { id: 'o3', label: 'o3', pillLabel: 'o3' },
  ],
  anthropic: [
    {
      id: 'claude-sonnet-4-20250514',
      label: 'claude-sonnet-4-20250514',
      pillLabel: 'claude-sonnet-4-20250514',
      isDefault: true,
    },
    {
      id: 'claude-3-7-sonnet-latest',
      label: 'claude-3-7-sonnet-latest',
      pillLabel: 'claude-3-7-sonnet-latest',
    },
    {
      id: 'claude-3-5-haiku-latest',
      label: 'claude-3-5-haiku-latest',
      pillLabel: 'claude-3-5-haiku-latest',
    },
  ],
  deepseek: [
    {
      id: 'deepseek-chat',
      label: 'deepseek-chat',
      pillLabel: 'deepseek-chat',
      isDefault: true,
    },
    {
      id: 'deepseek-reasoner',
      label: 'deepseek-reasoner',
      pillLabel: 'deepseek-reasoner',
    },
  ],
  ollama: [],
};

const CLI_DEFAULT_SENTINELS = new Set([
  '',
  'default',
  'auto',
  'agy-cli',
  'agy-default',
  'codex-cli',
  'codex-default',
  'claude-code-cli',
  'claude-default',
]);

export function isCliProvider(provider: AiProviderKey): boolean {
  return (
    provider === 'antigravity_cli' ||
    provider === 'codex_cli' ||
    provider === 'claude_code_cli'
  );
}

export function isCliDefaultModel(modelName: string): boolean {
  return CLI_DEFAULT_SENTINELS.has(modelName.trim().toLowerCase());
}

export function getProviderModelPresets(
  provider: AiProviderKey,
  ollamaModels: string[] = []
): ProviderModelPreset[] {
  if (provider === 'ollama') {
    return ollamaModels.map((model, index) => ({
      id: model,
      label: model,
      pillLabel: model,
      isDefault: index === 0,
    }));
  }
  return PROVIDER_MODEL_PRESETS[provider] ?? [];
}

export function findProviderModelPreset(
  provider: AiProviderKey,
  modelName: string,
  ollamaModels: string[] = []
): ProviderModelPreset | undefined {
  const presets = getProviderModelPresets(provider, ollamaModels);
  const normalized = modelName.trim().toLowerCase();
  if (!normalized || (isCliProvider(provider) && isCliDefaultModel(normalized))) {
    return presets.find((p) => p.isDefault) ?? presets[0];
  }
  return presets.find((p) => p.id.toLowerCase() === normalized);
}

export function formatProviderPillLabel(
  provider: AiProviderKey,
  modelName: string,
  ollamaModels: string[] = []
): string {
  const trimmed = modelName.trim();
  const preset = findProviderModelPreset(provider, trimmed, ollamaModels);
  if (preset) {
    return preset.pillLabel;
  }

  switch (provider) {
    case 'antigravity_cli':
      return isCliDefaultModel(trimmed) ? 'agy CLI' : `agy · ${trimmed}`;
    case 'codex_cli':
      return isCliDefaultModel(trimmed) ? 'Codex CLI' : `Codex · ${trimmed}`;
    case 'claude_code_cli':
      return isCliDefaultModel(trimmed) ? 'Claude CLI' : `Claude · ${trimmed}`;
    case 'ollama':
      return trimmed || 'Ollama';
    default:
      return trimmed || provider;
  }
}
