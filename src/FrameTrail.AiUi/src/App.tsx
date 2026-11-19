import React from 'react';
import { TooltipProvider } from '@/components/ui/tooltip';
import { ChatThread } from '@/components/ChatThread';
import { Composer } from '@/components/Composer';
import { CopilotHeader } from '@/components/CopilotHeader';
import { SettingsDrawer } from '@/components/SettingsDrawer';
import { useEditorShortcuts } from '@/hooks/useEditorShortcuts';
import { AiCopilotProvider } from '@/store/useAiCopilotStore';

const AiCopilotShell: React.FC = () => {
  useEditorShortcuts();

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background text-foreground">
      <CopilotHeader />
      <ChatThread />
      <Composer />
      <SettingsDrawer />
    </div>
  );
};

export const App: React.FC = () => {
  return (
    <TooltipProvider delayDuration={250}>
      <AiCopilotProvider>
        <AiCopilotShell />
      </AiCopilotProvider>
    </TooltipProvider>
  );
};

export default App;
