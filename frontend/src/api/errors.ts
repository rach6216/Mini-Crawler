import type { ProblemDetails } from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | undefined;
  readonly errors: Record<string, string[]>;

  constructor(status: number, problem: ProblemDetails = {}) {
    const title = problem.title ?? defaultTitle(status);
    super(problem.detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = problem.detail;
    this.errors = problem.errors ?? {};
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isValidation(): boolean {
    return this.status === 400 && Object.keys(this.errors).length > 0;
  }

  /** Server error keys are PascalCase or camelCase depending on serializer settings; match case-insensitively. */
  fieldErrors(field: string): string[] {
    const wanted = field.toLowerCase();
    return Object.entries(this.errors)
      .filter(([key]) => key.toLowerCase() === wanted || key.toLowerCase() === `$.${wanted}`)
      .flatMap(([, messages]) => messages);
  }
}

/** Thrown when the request never produced an HTTP response (API down, DNS, CORS). */
export class NetworkError extends Error {
  constructor(cause: unknown) {
    super('Could not reach the crawler API. Check that it is running and try again.');
    this.name = 'NetworkError';
    this.cause = cause;
  }
}

function defaultTitle(status: number): string {
  if (status === 404) return 'Not found';
  if (status === 409) return 'Conflict';
  if (status >= 500) return 'Server error';
  return `Request failed (${status})`;
}

export function describeError(error: unknown): { title: string; detail?: string } {
  if (error instanceof ApiError) return { title: error.title, detail: error.detail };
  if (error instanceof NetworkError) return { title: 'API unreachable', detail: error.message };
  if (error instanceof Error) return { title: 'Unexpected error', detail: error.message };
  return { title: 'Unexpected error' };
}
