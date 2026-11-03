import type { ApplicationRoles } from "@/lib/roles";
import type { Choice } from "./ui/multi-select";

/** Roles as multi-select choices, listed under their application's key. */
export function roleChoices(choices: ApplicationRoles[]): Choice[] {
  return choices.flatMap(choice => choice.roles.map(role => ({
    value: role.id,
    label: role.name,
    group: choice.application.key,
    detail: role.description ?? undefined,
  })));
}
