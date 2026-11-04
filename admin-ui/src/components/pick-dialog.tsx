"use client";

import { useActionState, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Plus } from "lucide-react";
import type { FormState } from "@/lib/admin-requests";
import { Refusal } from "./form-parts";
import { Button } from "./ui/button";
import { Dialog, DialogActions } from "./ui/dialog";
import { MultiSelect, type Choice } from "./ui/multi-select";

interface PickProps {
  action: (previous: FormState<undefined>, form: FormData) => Promise<FormState<undefined>>;
  /** Fields sent along, e.g. the user's id. */
  hidden: Record<string, string>;
  /** The form field each pick is sent as. */
  name: string;
  label: string;
  placeholder: string;
  choices: Choice[];
  submitLabel: (count: number) => string;
  note?: ReactNode;
}

/**
 * A button that opens a dialog with a multi-select; the picks go to `action` together (e.g. roles to assign,
 * members to add, operations to grant). The dialog closes once the change was made, and stays open with the reason
 * after a refusal. With nothing left to pick, the button is disabled and the reason is written next to it.
 */
export function PickDialog({ triggerLabel, title, description, nothingLeft, ...pick }: PickProps & {
  triggerLabel: string;
  title: string;
  description: string;
  nothingLeft: string;
}) {
  const [open, setOpen] = useState(false);
  // Counts the openings: each one starts a new form (its key), with nothing picked and no refusal from last time.
  const [openings, setOpenings] = useState(0);

  // Not a tooltip: a disabled button gets no hover or focus, so nobody would ever see it.
  if (pick.choices.length === 0) {
    return (
      <div className="flex items-center gap-3">
        <span className="text-sm text-slate-500">{nothingLeft}</span>
        <Button size="sm" disabled><Plus aria-hidden />{triggerLabel}</Button>
      </div>
    );
  }

  function openChange(next: boolean) {
    setOpen(next);
    if (next) {
      setOpenings(count => count + 1);
    }
  }

  const trigger = <Button size="sm"><Plus aria-hidden />{triggerLabel}</Button>;
  return (
    <Dialog open={open} onOpenChange={openChange} title={title} description={description} trigger={trigger}>
      <PickForm key={openings} {...pick} close={() => setOpen(false)} />
    </Dialog>
  );
}

function PickForm({ action, hidden, name, label, placeholder, choices, submitLabel, note, close }: PickProps & { close: () => void }) {
  const tf = useTranslations("Forms");
  const [picked, setPicked] = useState<string[]>([]);
  const [state, submit, pending] = useActionState(async (previous: FormState<undefined>, form: FormData) => {
    const outcome = await action(previous, form);
    if (outcome?.ok) {
      close();
    }

    return outcome;
  }, undefined);

  // After a refusal part of the way through (one request per pick, stopping at the first refusal), the picks already
  // applied are no longer among the choices: they drop out, and what is left is what still has to be done.
  const stillPicked = picked.filter(value => choices.some(choice => choice.value === value));

  return (
    <form action={submit} className="grid gap-4">
      {Object.entries(hidden).map(([field, value]) => <input key={field} type="hidden" name={field} value={value} />)}
      {/* Not a Field: a <label> may hold only one control, and the multi-select has several buttons. */}
      <div className="grid gap-1.5">
        <span className="text-sm font-medium text-slate-700">{label}</span>
        <MultiSelect name={name} label={label} placeholder={placeholder} choices={choices} value={stillPicked} onChange={setPicked} />
      </div>
      {note}
      <Refusal state={state} />
      <DialogActions>
        <Button type="button" variant="secondary" onClick={close}>{tf("cancel")}</Button>
        <Button type="submit" busy={pending} disabled={stillPicked.length === 0}>{submitLabel(stillPicked.length)}</Button>
      </DialogActions>
    </form>
  );
}
