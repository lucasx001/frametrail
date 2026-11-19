import React from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import App from '@/App';
import { bridgeClient } from '@/bridge/client';
import { FALLBACK_STRINGS } from '@/i18n/strings';

function initEnglishHost(autoRespondTurns = false) {
  const mock = bridgeClient.resetForTests({ autoRespondTurns });
  render(<App />);

  act(() => {
    mock.dispatchFromHost({
      type: 'init',
      payload: {
        theme: 'dark',
        locale: 'en',
        strings: FALLBACK_STRINGS.en,
        context: {
          playheadSec: 4.2,
          playheadLabel: '00:04.2',
          durationSec: 30,
          durationLabel: '00:30.0',
          selectedClipIndex: 0,
          clipCount: 2,
          canvasWidth: 1920,
          canvasHeight: 1080,
          hasPointer: true,
        },
        settings: {
          provider: 'openai',
          modelName: 'gpt-4o',
          baseUrl: '',
          baseUrlPlaceholder: 'https://api.openai.com/v1',
          autoApply: false,
          hasApiKey: true,
          providerHasApiKey: {
            openai: true,
            anthropic: false,
            deepseek: false,
          },
        },
        env: {
          sidecarOnline: false,
          sidecarStarting: false,
          hasAntigravityCli: true,
          hasClaudeCodeCli: false,
          hasCodexCli: false,
          hasOllamaServer: true,
          ollamaModels: ['qwen2.5-coder:14b'],
        },
      },
    });
  });

  return mock;
}

describe('FrameTrail AI Copilot UI', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('streams reasoning, tool calls, markdown tokens, and triggers timeline.seek on timestamp click', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    expect(mock.getSentMessages()).toContainEqual({ type: 'ui.ready' });

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.started',
        payload: { turnId: 'turn-1', userPrompt: 'Zoom in at 4.2s' },
      });
    });

    expect(screen.getByText('Zoom in at 4.2s')).toBeInTheDocument();
    expect(screen.getByText('Thinking & planning...')).toBeInTheDocument();

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.stream',
        payload: {
          turnId: 'turn-1',
          kind: 'thinking',
          text: 'Inspecting clip 1 pointer activity.',
        },
      });
      mock.dispatchFromHost({
        type: 'turn.stream',
        payload: {
          turnId: 'turn-1',
          kind: 'tool_start',
          toolName: 'inspect_timeline',
          text: 'Reading tracks',
        },
      });
    });

    expect(screen.getByText('inspect_timeline')).toBeInTheDocument();
    expect(screen.getByText('Running')).toBeInTheDocument();

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.stream',
        payload: {
          turnId: 'turn-1',
          kind: 'tool_end',
          toolName: 'inspect_timeline',
          text: 'Found 2 clips',
          succeeded: true,
        },
      });
      mock.dispatchFromHost({
        type: 'turn.stream',
        payload: {
          turnId: 'turn-1',
          kind: 'token',
          text: 'Added zoom at 00:04.2 to highlight the click.',
        },
      });
      mock.dispatchFromHost({
        type: 'turn.completed',
        payload: {
          turnId: 'turn-1',
          replyText: 'Added zoom at 00:04.2 to highlight the click.',
          elapsedSeconds: 1.4,
          status: 'done',
          toolCalls: [
            {
              toolName: 'inspect_timeline',
              summary: 'Found 2 clips',
              succeeded: true,
            },
          ],
        },
      });
    });

    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('Thought for 1.4s')).toBeInTheDocument();

    const seekBtn = await screen.findByTestId('seek-timestamp-btn');
    expect(seekBtn).toHaveTextContent('00:04.2');

    await user.click(seekBtn);
    expect(mock.getSentMessages()).toContainEqual({
      type: 'timeline.seek',
      payload: { seconds: 4.2 },
    });
  });

  it('handles TimelineChangesCard Review (Keep/Revert), Auto (Undo), and card timestamp seek', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.started',
        payload: { turnId: 'turn-2', userPrompt: 'Add zoom' },
      });
      mock.dispatchFromHost({
        type: 'turn.completed',
        payload: {
          turnId: 'turn-2',
          replyText: 'Done.',
          elapsedSeconds: 0.6,
          status: 'done',
          toolCalls: [],
        },
      });
      mock.dispatchFromHost({
        type: 'turn.changes',
        payload: {
          turnId: 'turn-2',
          state: 'previewing',
          items: [
            {
              badge: '+',
              label: 'Zoom 1.8×',
              detail: '00:04.2 – 00:06.2',
              seekSeconds: 4.2,
              timeLabel: '00:04.2',
            },
          ],
        },
      });
    });

    expect(screen.getByTestId('timeline-changes-status')).toHaveTextContent('Previewing');

    // Click timestamp pill inside card
    await user.click(screen.getByRole('button', { name: '00:04.2' }));
    expect(mock.getSentMessages()).toContainEqual({
      type: 'timeline.seek',
      payload: { seconds: 4.2 },
    });

    // Click Revert
    await user.click(screen.getByRole('button', { name: 'Revert' }));
    expect(mock.getSentMessages()).toContainEqual({
      type: 'changes.reject',
      payload: { turnId: 'turn-2' },
    });

    // Click Keep
    await user.click(screen.getByRole('button', { name: 'Keep' }));
    expect(mock.getSentMessages()).toContainEqual({
      type: 'changes.accept',
      payload: { turnId: 'turn-2' },
    });

    // Host updates state to applied with revision
    act(() => {
      mock.dispatchFromHost({
        type: 'turn.changes',
        payload: {
          turnId: 'turn-2',
          state: 'applied',
          revision: 12,
          items: [
            {
              badge: '+',
              label: 'Zoom 1.8×',
              detail: '00:04.2 – 00:06.2',
              seekSeconds: 4.2,
              timeLabel: '00:04.2',
            },
          ],
        },
      });
    });

    expect(screen.getByTestId('timeline-changes-status')).toHaveTextContent('Applied');
    expect(screen.getByText('Timeline Changes · #12')).toBeInTheDocument();

    // Click Undo Edit
    await user.click(screen.getByRole('button', { name: 'Undo Edit' }));
    expect(mock.getSentMessages()).toContainEqual({
      type: 'changes.undo',
      payload: { turnId: 'turn-2' },
    });
  });

  it('supports SlashCommandPalette filtering and ArrowUp/ArrowDown/Enter/Escape keyboard navigation', async () => {
    initEnglishHost(false);
    const user = userEvent.setup();

    const textarea = screen.getByPlaceholderText(/Ask AI to edit/i) as HTMLTextAreaElement;
    await user.click(textarea);
    await user.type(textarea, '/');

    const palette = screen.getByTestId('slash-command-palette');
    expect(palette).toBeInTheDocument();
    expect(screen.getByText('/silence')).toBeInTheDocument();
    expect(screen.getByText('/zoom')).toBeInTheDocument();
    expect(screen.getByText('/cut')).toBeInTheDocument();

    // Navigate down twice (/silence -> /clicks -> /zoom) then up once (-> /clicks), then filter
    fireEvent.keyDown(textarea, { key: 'ArrowDown' });
    fireEvent.keyDown(textarea, { key: 'ArrowDown' });
    expect(screen.getByText('/zoom').closest('[cmdk-item]')).toHaveAttribute(
      'data-selected',
      'true'
    );

    fireEvent.keyDown(textarea, { key: 'ArrowUp' });
    expect(screen.getByText('/clicks').closest('[cmdk-item]')).toHaveAttribute(
      'data-selected',
      'true'
    );

    // Filter to /zoom
    await user.type(textarea, 'zoom');
    expect(within(palette).queryByText('/silence')).not.toBeInTheDocument();
    expect(within(palette).getByText('/zoom')).toBeInTheDocument();

    // Press Enter to expand template into textarea
    fireEvent.keyDown(textarea, { key: 'Enter' });
    await waitFor(() => {
      expect(textarea.value).toBe('Add 1.8x zoom at @4.2s');
    });
    expect(screen.queryByTestId('slash-command-palette')).not.toBeInTheDocument();

    // Re-open with / and dismiss with Escape
    await user.clear(textarea);
    await user.type(textarea, '/');
    expect(screen.getByTestId('slash-command-palette')).toBeInTheDocument();
    fireEvent.keyDown(textarea, { key: 'Escape' });
    expect(screen.queryByTestId('slash-command-palette')).not.toBeInTheDocument();
  });

  it('keeps SettingsDrawer API key write-only and emits updateConfig / clearApiKey / sidecar.start', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: 'Model Settings' }));

    expect(screen.getByTestId('api-key-saved-badge')).toHaveTextContent('Saved & Encrypted');

    const keyInput = screen.getByLabelText('API Key') as HTMLInputElement;
    // Never displays plaintext API key
    expect(keyInput.value).toBe('');
    expect(keyInput.type).toBe('password');

    // Submit a new key
    await user.type(keyInput, 'sk-test-secret-123{Enter}');
    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.updateConfig',
      payload: { newApiKey: 'sk-test-secret-123' },
    });
    // Input clears immediately after submit
    expect(keyInput.value).toBe('');

    // Clear saved API key
    await user.click(screen.getByTestId('api-key-clear-btn'));
    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.updateConfig',
      payload: { clearApiKey: true },
    });

    // Start sidecar button
    await user.click(screen.getByRole('button', { name: 'Start Local Sidecar' }));
    expect(mock.getSentMessages()).toContainEqual({ type: 'sidecar.start' });
  });

  it('forwards editor shortcuts only when focus is outside editable elements and blocks browser reload', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    // Outside inputs -> should forward Space, Ctrl+Z, ArrowLeft
    fireEvent.keyDown(document.body, { key: ' ', code: 'Space' });
    fireEvent.keyDown(document.body, { key: 'z', ctrlKey: true });
    fireEvent.keyDown(document.body, { key: 'ArrowLeft' });

    expect(mock.getSentMessages()).toContainEqual({
      type: 'editor.shortcut',
      payload: { key: 'Space', ctrl: false, shift: false, alt: false },
    });
    expect(mock.getSentMessages()).toContainEqual({
      type: 'editor.shortcut',
      payload: { key: 'Z', ctrl: true, shift: false, alt: false },
    });
    expect(mock.getSentMessages()).toContainEqual({
      type: 'editor.shortcut',
      payload: { key: 'ArrowLeft', ctrl: false, shift: false, alt: false },
    });

    mock.clearSentMessages();

    // Inside textarea -> should NOT forward Space or Ctrl+Z
    const textarea = screen.getByPlaceholderText(/Ask AI to edit/i);
    await user.click(textarea);
    fireEvent.keyDown(textarea, { key: ' ', code: 'Space' });
    fireEvent.keyDown(textarea, { key: 'z', ctrlKey: true });

    expect(mock.getSentMessages().some((m) => m.type === 'editor.shortcut')).toBe(false);

    // F5 and Ctrl+R should be prevented
    const f5 = new KeyboardEvent('keydown', { key: 'F5', cancelable: true });
    window.dispatchEvent(f5);
    expect(f5.defaultPrevented).toBe(true);

    const ctrlR = new KeyboardEvent('keydown', { key: 'r', ctrlKey: true, cancelable: true });
    window.dispatchEvent(ctrlR);
    expect(ctrlR.defaultPrevented).toBe(true);
  });

  it('renders ModelProviderPicker groups and switches providers, theme, and session state', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    await user.click(screen.getByTestId('model-picker-trigger'));
    expect(screen.getByText('Local Subscription CLIs')).toBeInTheDocument();
    expect(screen.getByText('Local Models (Ollama)')).toBeInTheDocument();
    expect(screen.getByText('Cloud APIs (BYOK)')).toBeInTheDocument();

    await user.click(screen.getByText('Ollama · qwen2.5-coder:14b'));
    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.selectProvider',
      payload: { provider: 'ollama', modelOverride: 'qwen2.5-coder:14b' },
    });

    act(() => {
      mock.dispatchFromHost({ type: 'theme.changed', payload: { theme: 'light' } });
    });
    expect(document.documentElement.classList.contains('dark')).toBe(false);
    expect(document.documentElement.style.backgroundColor).toMatch(/255,\s*255,\s*255|#ffffff/i);

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.started',
        payload: { turnId: 'turn-x', userPrompt: 'Hello' },
      });
    });
    expect(screen.getByText('Hello')).toBeInTheDocument();

    act(() => {
      mock.dispatchFromHost({ type: 'session.cleared', payload: {} });
    });
    expect(screen.queryByText('Hello')).not.toBeInTheDocument();
  });

  it('allows selecting Antigravity CLI and OpenAI Codex CLI sub-models via sub-model picker and settings presets', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    // 1. Switch provider to Antigravity CLI (agy)
    await user.click(screen.getByTestId('model-picker-trigger'));
    await user.click(screen.getByText('Antigravity CLI (agy)'));

    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.selectProvider',
      payload: { provider: 'antigravity_cli', modelOverride: 'agy-cli' },
    });

    act(() => {
      mock.dispatchFromHost({
        type: 'settings.updated',
        payload: {
          provider: 'antigravity_cli',
          modelName: 'agy-cli',
          baseUrl: '',
          baseUrlPlaceholder: 'Local Google Antigravity CLI (agy)',
          autoApply: false,
          hasApiKey: false,
          providerHasApiKey: { openai: true },
        },
      });
    });

    expect(screen.getByTestId('model-picker-trigger')).toHaveTextContent('agy CLI');
    expect(screen.getByTestId('cli-submodel-picker-trigger')).toHaveTextContent(
      'CLI Default (Auto)'
    );

    // 2. Pick specific Antigravity CLI sub-model via dedicated sub-model picker
    await user.click(screen.getByTestId('cli-submodel-picker-trigger'));
    await user.click(screen.getByTestId('submodel-option-gemini-4-argon-high'));

    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.selectProvider',
      payload: {
        provider: 'antigravity_cli',
        modelOverride: 'gemini-4-argon-high',
      },
    });

    act(() => {
      mock.dispatchFromHost({
        type: 'settings.updated',
        payload: {
          provider: 'antigravity_cli',
          modelName: 'gemini-4-argon-high',
          baseUrl: '',
          baseUrlPlaceholder: 'Local Google Antigravity CLI (agy)',
          autoApply: false,
          hasApiKey: false,
          providerHasApiKey: { openai: true },
        },
      });
    });

    expect(screen.getByTestId('model-picker-trigger')).toHaveTextContent('agy · Argon (High)');
    expect(screen.getByTestId('cli-submodel-picker-trigger')).toHaveTextContent(
      'Gemini 4 Argon (High)'
    );

    // 3. Switch to OpenAI Codex CLI and pick gpt-5.3-codex via SettingsDrawer preset pill
    await user.click(screen.getByTestId('model-picker-trigger'));
    await user.click(screen.getByText('OpenAI Codex CLI'));

    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.selectProvider',
      payload: { provider: 'codex_cli', modelOverride: 'codex-cli' },
    });

    act(() => {
      mock.dispatchFromHost({
        type: 'settings.updated',
        payload: {
          provider: 'codex_cli',
          modelName: 'codex-cli',
          baseUrl: '',
          baseUrlPlaceholder: 'Local OpenAI Codex CLI',
          autoApply: false,
          hasApiKey: false,
          providerHasApiKey: { openai: true },
        },
      });
    });

    expect(screen.getByTestId('model-picker-trigger')).toHaveTextContent('Codex CLI');

    await user.click(screen.getByRole('button', { name: 'Model Settings' }));
    const codexPresetBtn = screen.getByTestId('settings-model-preset-gpt-5.3-codex');
    await user.click(codexPresetBtn);

    expect(mock.getSentMessages()).toContainEqual({
      type: 'settings.updateConfig',
      payload: { modelName: 'gpt-5.3-codex' },
    });

    act(() => {
      mock.dispatchFromHost({
        type: 'settings.updated',
        payload: {
          provider: 'codex_cli',
          modelName: 'gpt-5.3-codex',
          baseUrl: '',
          baseUrlPlaceholder: 'Local OpenAI Codex CLI',
          autoApply: false,
          hasApiKey: false,
          providerHasApiKey: { openai: true },
        },
      });
    });

    expect(screen.getByTestId('model-picker-trigger')).toHaveTextContent('Codex · GPT-5.3');
  });

  it('supports Voice Input via button click & Ctrl+M, streams interim/final transcripts, and configures Voice Input settings', async () => {
    const mock = initEnglishHost(false);
    const user = userEvent.setup();

    const voiceBtn = screen.getByTestId('voice-input-btn');
    const textarea = screen.getByPlaceholderText(/Ask AI to edit/i) as HTMLTextAreaElement;

    // 1. Click voice input button to start recording
    await user.click(voiceBtn);
    expect(mock.getSentMessages()).toContainEqual({
      type: 'voice.start',
      payload: { lang: 'en-US' },
    });
    expect(voiceBtn).toHaveAttribute('data-listening', 'true');
    expect(screen.getByTestId('voice-listening-badge')).toHaveTextContent('Listening…');

    // 2. Receive interim transcript
    act(() => {
      mock.dispatchFromHost({
        type: 'voice.state',
        payload: { state: 'listening' },
      });
      mock.dispatchFromHost({
        type: 'voice.transcript',
        payload: { text: 'Add 1.8x zoom...', isFinal: false },
      });
    });

    expect(screen.getByTestId('voice-listening-badge')).toHaveTextContent('Add 1.8x zoom...');

    // 3. Receive final transcript -> appended to textarea
    act(() => {
      mock.dispatchFromHost({
        type: 'voice.transcript',
        payload: { text: 'Add 1.8x zoom at 00:04.2', isFinal: true },
      });
    });

    expect(textarea.value).toBe('Add 1.8x zoom at 00:04.2');

    // 4. Press Ctrl+M inside textarea to stop voice recording
    fireEvent.keyDown(textarea, { key: 'm', ctrlKey: true });
    expect(mock.getSentMessages()).toContainEqual({
      type: 'voice.stop',
    });
    expect(voiceBtn).toHaveAttribute('data-listening', 'false');

    // 5. Open SettingsDrawer and configure Antigravity-style Voice Input settings
    await user.click(screen.getByRole('button', { name: 'Model Settings' }));
    expect(screen.getByTestId('voice-settings-section')).toBeInTheDocument();
    expect(screen.getByTestId('voice-shortcut-hint')).toHaveTextContent('Shortcut: Ctrl + M');

    await user.click(screen.getByTestId('voice-lang-zh-CN'));
    await user.click(screen.getByTestId('voice-autosubmit-toggle'));

    const savedSettings = JSON.parse(
      window.localStorage.getItem('frametrail.ai.voiceSettings') || '{}'
    );
    expect(savedSettings.language).toBe('zh-CN');
    expect(savedSettings.autoSubmit).toBe(true);

    // 6. Close SettingsDrawer, then test Ctrl+M start + final transcript + stop with autoSubmit enabled
    await user.keyboard('{Escape}');
    mock.clearSentMessages();
    fireEvent.change(textarea, { target: { value: '' } });

    fireEvent.keyDown(window, { key: 'm', ctrlKey: true });
    expect(mock.getSentMessages()).toContainEqual({
      type: 'voice.start',
      payload: { lang: 'zh-CN' },
    });

    act(() => {
      mock.dispatchFromHost({
        type: 'voice.transcript',
        payload: { text: '清理静音片段', isFinal: true },
      });
    });
    expect(textarea.value).toBe('清理静音片段');

    fireEvent.keyDown(window, { key: 'm', ctrlKey: true });
    expect(mock.getSentMessages()).toContainEqual({
      type: 'voice.stop',
    });
    await waitFor(() => {
      expect(mock.getSentMessages()).toContainEqual({
        type: 'chat.send',
        payload: { prompt: '清理静音片段' },
      });
    });
  });

  it('marks running tool calls as Error (not Completed) when a turn fails or is cancelled', () => {
    const mock = initEnglishHost(false);

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.started',
        payload: { turnId: 'turn-err', userPrompt: 'Generate subtitles' },
      });
      mock.dispatchFromHost({
        type: 'turn.stream',
        payload: {
          turnId: 'turn-err',
          kind: 'tool_start',
          toolName: 'agy_cli',
          text: 'Invoking local CLI session',
        },
      });
    });

    expect(screen.getByText('agy_cli')).toBeInTheDocument();
    expect(screen.getByText('Running')).toBeInTheDocument();

    act(() => {
      mock.dispatchFromHost({
        type: 'turn.completed',
        payload: {
          turnId: 'turn-err',
          replyText: '**AI Edit Failed**: agy timed out after 180s.',
          elapsedSeconds: 180.5,
          status: 'error',
          toolCalls: [],
        },
      });
    });

    expect(screen.getByText('Error')).toBeInTheDocument();
    expect(screen.queryByText('Completed')).not.toBeInTheDocument();
  });
});
