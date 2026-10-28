import { ArrowRight, ChevronRight } from 'lucide-react';
import { useId, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError, describeError } from '../api/errors';
import { useCreateJob } from '../api/queries';
import type { CreateJobRequest } from '../api/types';
import { Alert } from '../components/Alert';
import { Card } from '../components/Card';
import { Spinner } from '../components/Spinner';

const LIMITS = {
  depth: { min: 0, max: 5, default: 2 },
  pages: { min: 1, max: 1000, default: 200 },
} as const;

type Field = 'url' | 'maxDepth' | 'maxPages';
type FieldErrors = Partial<Record<Field, string[]>>;
const FIELDS: readonly Field[] = ['url', 'maxDepth', 'maxPages'];

interface FormValues {
  url: string;
  maxDepth: string;
  maxPages: string;
}

function validateForm(values: FormValues): { request?: CreateJobRequest; errors: FieldErrors } {
  const errors: FieldErrors = {};
  const url = values.url.trim();
  if (!url) {
    errors.url = ['Enter the URL to start crawling from.'];
  } else {
    let parsed: URL | null = null;
    try {
      parsed = new URL(url);
    } catch {
      parsed = null;
    }
    if (!parsed || (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') || !parsed.hostname) {
      errors.url = ['Enter an absolute URL starting with http:// or https://.'];
    }
  }

  const maxDepth = parseOptionalInt(values.maxDepth);
  if (Number.isNaN(maxDepth) || (maxDepth !== undefined && (maxDepth < LIMITS.depth.min || maxDepth > LIMITS.depth.max))) {
    errors.maxDepth = [`Use a whole number from ${LIMITS.depth.min} to ${LIMITS.depth.max}.`];
  }
  const maxPages = parseOptionalInt(values.maxPages);
  if (Number.isNaN(maxPages) || (maxPages !== undefined && (maxPages < LIMITS.pages.min || maxPages > LIMITS.pages.max))) {
    errors.maxPages = [`Use a whole number from ${LIMITS.pages.min} to ${LIMITS.pages.max}.`];
  }

  if (Object.keys(errors).length > 0) return { errors };
  const request: CreateJobRequest = { url };
  if (maxDepth !== undefined) request.maxDepth = maxDepth;
  if (maxPages !== undefined) request.maxPages = maxPages;
  return { request, errors };
}

/** Empty means "use the server default"; anything else must be an integer. */
function parseOptionalInt(raw: string): number | undefined {
  const trimmed = raw.trim();
  if (trimmed === '') return undefined;
  return /^-?\d+$/.test(trimmed) ? Number(trimmed) : Number.NaN;
}

function serverFieldErrors(error: unknown): { fields: FieldErrors; unmapped: string[] } {
  if (!(error instanceof ApiError)) return { fields: {}, unmapped: [] };
  const fields: FieldErrors = {};
  for (const field of FIELDS) {
    const messages = error.fieldErrors(field);
    if (messages.length > 0) fields[field] = messages;
  }
  const known = new Set(FIELDS.map((f) => f.toLowerCase()));
  const unmapped = Object.entries(error.errors)
    .filter(([key]) => !known.has(key.replace(/^\$\./, '').toLowerCase()))
    .flatMap(([, messages]) => messages);
  return { fields, unmapped };
}

export function StartCrawlPage() {
  const navigate = useNavigate();
  const createJob = useCreateJob();
  const [values, setValues] = useState<FormValues>({
    url: '',
    maxDepth: String(LIMITS.depth.default),
    maxPages: String(LIMITS.pages.default),
  });
  const [clientErrors, setClientErrors] = useState<FieldErrors>({});
  const ids = { url: useId(), maxDepth: useId(), maxPages: useId() };

  const server = serverFieldErrors(createJob.error);
  const errorsFor = (field: Field) => clientErrors[field] ?? server.fields[field];
  const hasServerFieldErrors = Object.keys(server.fields).length > 0;
  const showBanner = createJob.isError && !hasServerFieldErrors;
  const advancedHasError = !!errorsFor('maxPages');

  const update = (field: Field, value: string) => {
    setValues((v) => ({ ...v, [field]: value }));
    if (clientErrors[field]) setClientErrors((e) => ({ ...e, [field]: undefined }));
  };

  const onSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (createJob.isPending) return;
    const { request, errors } = validateForm(values);
    setClientErrors(errors);
    if (!request) return;
    createJob.mutate(request, {
      onSuccess: ({ jobId }) => navigate(`/jobs/${jobId}`),
    });
  };

  const describedBy = (field: Field, helpId?: string) =>
    [helpId, errorsFor(field) ? `${ids[field]}-error` : undefined].filter(Boolean).join(' ') || undefined;

  return (
    <div className="page page--narrow">
      <header className="page-header">
        <h1 className="page-title">Start a crawl</h1>
        <p className="page-description">
          Crawl a site breadth-first from a start URL, staying on the same host, and inspect the resulting page tree.
        </p>
      </header>

      <Card>
        <form className="form" onSubmit={onSubmit} noValidate>
          {showBanner && (
            <Alert tone="danger" title={describeError(createJob.error).title}>
              {describeError(createJob.error).detail}
              {server.unmapped.length > 0 && (
                <ul className="alert__list">
                  {server.unmapped.map((m) => (
                    <li key={m}>{m}</li>
                  ))}
                </ul>
              )}
            </Alert>
          )}

          <div className="field">
            <label htmlFor={ids.url} className="field__label">
              Start URL <span className="field__required" aria-hidden>*</span>
            </label>
            <input
              id={ids.url}
              className="input input--mono"
              type="url"
              inputMode="url"
              autoComplete="url"
              spellCheck={false}
              placeholder="https://example.com"
              value={values.url}
              onChange={(e) => update('url', e.target.value)}
              aria-required
              aria-invalid={!!errorsFor('url')}
              aria-describedby={describedBy('url')}
              autoFocus
            />
            <FieldError id={`${ids.url}-error`} messages={errorsFor('url')} />
          </div>

          <div className="field field--short">
            <label htmlFor={ids.maxDepth} className="field__label">
              Max depth
            </label>
            <input
              id={ids.maxDepth}
              className="input"
              type="number"
              min={LIMITS.depth.min}
              max={LIMITS.depth.max}
              step={1}
              value={values.maxDepth}
              onChange={(e) => update('maxDepth', e.target.value)}
              aria-invalid={!!errorsFor('maxDepth')}
              aria-describedby={describedBy('maxDepth', `${ids.maxDepth}-help`)}
            />
            <p id={`${ids.maxDepth}-help`} className="field__help">
              Link hops from the start page ({LIMITS.depth.min}–{LIMITS.depth.max}). 0 crawls only the start page. Leave
              empty for the default ({LIMITS.depth.default}).
            </p>
            <FieldError id={`${ids.maxDepth}-error`} messages={errorsFor('maxDepth')} />
          </div>

          <details className="advanced" open={advancedHasError || undefined}>
            <summary className="advanced__summary">
              <ChevronRight size={16} aria-hidden className="advanced__chevron" />
              Advanced
            </summary>
            <div className="field field--short">
              <label htmlFor={ids.maxPages} className="field__label">
                Max pages
              </label>
              <input
                id={ids.maxPages}
                className="input"
                type="number"
                min={LIMITS.pages.min}
                max={LIMITS.pages.max}
                step={1}
                value={values.maxPages}
                onChange={(e) => update('maxPages', e.target.value)}
                aria-invalid={!!errorsFor('maxPages')}
                aria-describedby={describedBy('maxPages', `${ids.maxPages}-help`)}
              />
              <p id={`${ids.maxPages}-help`} className="field__help">
                Upper bound on pages stored for this job ({LIMITS.pages.min}–{LIMITS.pages.max}, default{' '}
                {LIMITS.pages.default}). The crawl is marked truncated if more in-scope pages exist.
              </p>
              <FieldError id={`${ids.maxPages}-error`} messages={errorsFor('maxPages')} />
            </div>
          </details>

          <div className="form__actions">
            <button type="submit" className="btn btn--primary" disabled={createJob.isPending}>
              {createJob.isPending ? <Spinner size={16} /> : <ArrowRight size={16} aria-hidden />}
              {createJob.isPending ? 'Starting…' : 'Start crawl'}
            </button>
          </div>
        </form>
      </Card>
    </div>
  );
}

function FieldError({ id, messages }: { id: string; messages: string[] | undefined }) {
  if (!messages || messages.length === 0) return null;
  return (
    <p id={id} className="field__error" role="alert">
      {messages.join(' ')}
    </p>
  );
}
