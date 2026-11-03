"use client";

import { useActionState, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Plus } from "lucide-react";
import type { FormState } from "@/lib/admin-requests";
import { Refusal } from "./form-parts";
import { Button } from "./ui/button";
import { Dialog, DialogActions } from "./ui/dialog";
import { Field } from "./ui/fields";
import { MultiSelect, type Choice } from "./ui/multi-select";

/**
 * A button that opens a dialog with a multi-select; the picks go to `action` together (e.g. roles to assign,
 * members to add, operations to grant). The dialog closes once the change was made, and stays open with the reason
 * after a refusal. With nothing left to pick, the button is disabled and says why.
 */
export function PickDialog({ triggerLabel, title, description, action, hidden, name, label, placeholder, choices, submitLabel, nothingLeft, note }: {
  triggerLabel: string;
  title: string;
  description: string;
  action: (previous: FormState<undefined>, form: FormData) => Promise<FormState<undefined>>;
  /** Fields sent along, e.g. the user's id. */
  hidden: Record<string, string>;
  /** The form field each pick is sent as. */
  name: string;
  label: string;
  placeholder: string;
  choices: Choice[];
  submitLabel: (count: number) => string;
  nothingLeft: string;
  note?: ReactNode;
}) {
  const tf = useTranslations("Forms");
  const [open, setOpen] = useState(false);
  const [picked, setPicked] = useState(0);
  const [state, submit, pending] = useActionState(async (previous: FormState<undefined>, form: FormData) => {
    const outcome = await action(previous, form);
    if (outcome?.ok) {
      setOpen(false);
    }

    return outcome;
  }, undefined);

  function openChange(next: boolean) {
    setOpen(next);
    // The multi-select starts empty each time the dialog opens.
    setPicked(0);
  }

  const trigger = (
    <Button size="sm" disabled={choices.length === 0} title={choices.length === 0 ? nothingLeft : undefined}>
      <Plus aria-hidden />{triggerLabel}
    </Button>
  );

  return (
    <Dialog open={open} onOpenChange={openChange} title={title} description={description} trigger={trigger}>
      <form action={submit} className="grid gap-4">
        {Object.entries(hidden).map(([field, value]) => <input key={field} type="hidden" name={field} value={value} />)}
        <Field label={label}>
          <MultiSelect name={name} label={label} placeholder={placeholder} choices={choices} onChange={values => setPicked(values.length)} />
        </Field>
        {note}
        <Refusal state={state} />
        <DialogActions>
          <Button type="button" variant="secondary" onClick={() => openChange(false)}>{tf("cancel")}</Button>
          <Button type="submit" disabled={pending || picked === 0}>{submitLabel(picked)}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
