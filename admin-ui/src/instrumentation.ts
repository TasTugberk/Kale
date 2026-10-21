/** Runs once when the server starts. The checks use Node.js, so they live in their own file (Next.js's pattern). */
export async function register() {
  if (process.env.NEXT_RUNTIME === "nodejs") {
    await import("./instrumentation-node");
  }
}
