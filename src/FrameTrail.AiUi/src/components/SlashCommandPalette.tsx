import React from 'react';
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandItem,
  CommandList,
} from '@/components/ui/command';
import type { LocaleCode } from '@/bridge/protocol';
import type { SlashCommandItem } from '@/lib/slashCommands';
import { cn } from '@/lib/utils';

interface SlashCommandPaletteProps {
  open: boolean;
  items: SlashCommandItem[];
  activeIndex: number;
  locale: LocaleCode;
  emptyText: string;
  onActiveIndexChange: (index: number) => void;
  onSelect: (item: SlashCommandItem) => void;
}

export const SlashCommandPalette: React.FC<SlashCommandPaletteProps> = ({
  open,
  items,
  activeIndex,
  locale,
  emptyText,
  onActiveIndexChange,
  onSelect,
}) => {
  if (!open) return null;

  const activeCommand = items[activeIndex]?.command ?? '';

  return (
    <div
      data-testid="slash-command-palette"
      className="mx-3 mb-1 overflow-hidden rounded-xl border border-border bg-card shadow-md"
    >
      <Command shouldFilter={false} value={activeCommand} className="bg-transparent">
        <CommandList className="max-h-56 p-1.5">
          {items.length === 0 ? (
            <CommandEmpty className="py-2 text-center text-[11px] text-muted-foreground">
              {emptyText}
            </CommandEmpty>
          ) : (
            <CommandGroup className="p-0">
              {items.map((item, idx) => {
                const isSelected = idx === activeIndex;
                const desc = locale === 'en' ? item.descriptionEn : item.descriptionZh;
                return (
                  <CommandItem
                    key={item.command}
                    value={item.command}
                    aria-selected={isSelected}
                    data-selected={isSelected ? 'true' : undefined}
                    onMouseEnter={() => onActiveIndexChange(idx)}
                    onSelect={() => onSelect(item)}
                    className={cn(
                      'flex h-7 cursor-pointer items-center gap-2 rounded-md px-2 py-0 text-[11px]',
                      isSelected && 'bg-accent text-accent-foreground'
                    )}
                  >
                    <span className="shrink-0 font-mono text-[11px] font-semibold text-foreground">
                      {item.command}
                    </span>
                    <span className="truncate text-[11px] text-muted-foreground">{desc}</span>
                  </CommandItem>
                );
              })}
            </CommandGroup>
          )}
        </CommandList>
      </Command>
    </div>
  );
};
