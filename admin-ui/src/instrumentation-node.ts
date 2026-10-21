import { timeZoneFrom } from "./lib/config";

// Settings every page needs are checked at startup: a wrong one stops the server with its reason, instead of a
// server that looks healthy while every page fails. (Next.js only logs an error thrown during startup and keeps
// serving, so this exits itself, and the container fails visibly.)
try {
  timeZoneFrom({ KALE_TIME_ZONE: process.env.KALE_TIME_ZONE });
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
}
