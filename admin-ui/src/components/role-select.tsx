"use client";

import type { ApplicationRoles } from "@/lib/roles";

/** A role to pick, grouped by application (applications without roles to pick are left out). */
export function RoleSelect({ choices, label }: { choices: ApplicationRoles[]; label: string }) {
  return (
    <select name="roleId" aria-label={label} required>
      {choices.filter(choice => choice.roles.length > 0).map(choice => (
        <optgroup key={choice.application.id} label={choice.application.key}>
          {choice.roles.map(role => <option key={role.id} value={role.id}>{role.name}</option>)}
        </optgroup>
      ))}
    </select>
  );
}
