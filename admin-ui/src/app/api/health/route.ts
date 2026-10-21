/** For the container healthcheck: liveness only. */
export function GET() {
  return new Response("Healthy");
}
