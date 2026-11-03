"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Popover } from "radix-ui";
import { Command } from "cmdk";
import { Check, ChevronsUpDown, X } from "lucide-react";
import { cn } from "./cn";

export interface Choice {
  value: string;
  label: string;
  /** Choices with the same group are listed under it (e.g. roles under their application's key). */
  group?: string;
  /** A second line, e.g. a user's display name. */
  detail?: string;
}

/**
 * A searchable list to pick several items from. The picked values are posted with the surrounding form as one
 * hidden field per value, all named `name` (read them with listFromForm).
 */
export function MultiSelect({ name, choices, label, placeholder, onChange }: {
  name: string;
  choices: Choice[];
  label: string;
  placeholder: string;
  onChange?: (values: string[]) => void;
}) {
  const t = useTranslations("Forms");
  const [open, setOpen] = useState(false);
  const [picked, setPicked] = useState<string[]>([]);

  function update(values: string[]) {
    setPicked(values);
    onChange?.(values);
  }

  function toggle(value: string) {
    update(picked.includes(value) ? picked.filter(v => v !== value) : [...picked, value]);
  }

  // Groups in the order their first choice comes; choices without one are listed under no heading.
  const groups = [...new Set(choices.map(choice => choice.group ?? ""))];
  const labelOf = (value: string) => choices.find(choice => choice.value === value);

  return (
    <div className="grid gap-2">
      {picked.map(value => <input key={value} type="hidden" name={name} value={value} />)}
      <Popover.Root open={open} onOpenChange={setOpen}>
        <Popover.Trigger asChild>
          <button
            type="button"
            role="combobox"
            aria-expanded={open}
            aria-label={label}
            className="flex h-9 w-full cursor-pointer items-center justify-between gap-2 rounded-md border border-slate-300 bg-white px-3 text-left text-sm shadow-sm hover:bg-slate-50 focus:border-brand-500 focus:ring-2 focus:ring-brand-500/20 focus:outline-none"
          >
            <span className={picked.length === 0 ? "text-slate-400" : "text-slate-900"}>
              {picked.length === 0 ? placeholder : t("picked", { count: picked.length })}
            </span>
            <ChevronsUpDown className="size-4 text-slate-400" aria-hidden />
          </button>
        </Popover.Trigger>
        <Popover.Portal>
          <Popover.Content align="start" sideOffset={4} className="z-50 w-(--radix-popover-trigger-width) rounded-md border border-slate-200 bg-white shadow-lg">
            <Command label={label} className="text-sm">
              <Command.Input
                placeholder={t("searchChoices")}
                className="h-10 w-full border-b border-slate-200 px-3 text-sm focus:outline-none"
              />
              <Command.List className="max-h-64 overflow-y-auto p-1">
                <Command.Empty className="px-3 py-6 text-center text-slate-500">{t("noChoices")}</Command.Empty>
                {groups.map(group => (
                  <Command.Group
                    key={group}
                    heading={group || undefined}
                    className="[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:pt-2 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:font-mono [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:text-slate-500"
                  >
                    {choices.filter(choice => (choice.group ?? "") === group).map(choice => (
                      <Command.Item
                        key={choice.value}
                        // The value identifies the item; searching goes by what the admin sees.
                        value={choice.value}
                        keywords={[choice.label, choice.detail ?? "", choice.group ?? ""]}
                        onSelect={() => toggle(choice.value)}
                        className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 data-[selected=true]:bg-slate-100"
                      >
                        <span className={cn(
                          "flex size-4 items-center justify-center rounded border",
                          picked.includes(choice.value) ? "border-brand-700 bg-brand-700 text-white" : "border-slate-300",
                        )}>
                          {picked.includes(choice.value) && <Check className="size-3" aria-hidden />}
                        </span>
                        <span className="min-w-0">
                          <span className="block truncate">{choice.label}</span>
                          {choice.detail && <span className="block truncate text-xs text-slate-500">{choice.detail}</span>}
                        </span>
                      </Command.Item>
                    ))}
                  </Command.Group>
                ))}
              </Command.List>
            </Command>
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
      {picked.length > 0 && (
        <ul className="flex flex-wrap gap-1.5" aria-label={t("pickedList")}>
          {picked.map(value => (
            <li key={value} className="inline-flex items-center gap-1 rounded-md bg-brand-50 py-0.5 pr-1 pl-2 text-xs font-medium text-brand-800">
              {labelOf(value)?.group && <span className="font-mono text-brand-600">{labelOf(value)?.group} ·</span>}
              {labelOf(value)?.label ?? value}
              <button
                type="button"
                onClick={() => toggle(value)}
                aria-label={t("unpick", { label: labelOf(value)?.label ?? value })}
                className="cursor-pointer rounded p-0.5 hover:bg-brand-100"
              >
                <X className="size-3" aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
