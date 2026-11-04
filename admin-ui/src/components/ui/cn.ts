import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

/** Joins class names; a later Tailwind class wins over an earlier one for the same property (e.g. a caller's padding). */
export function cn(...classes: ClassValue[]): string {
  return twMerge(clsx(classes));
}
