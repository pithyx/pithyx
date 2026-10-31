import type { PithyxApp } from '@pithyx/sdk';
import { Checkbox } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { storeCall, type AppAccess, type AppGrants, type SubjectGrant } from '../sdk/store';
import { usersCall } from '../sdk/users';
import { describeProblem } from '../storage/shared';
import { accessOf, withSubject } from './model';
import styles from './Store.module.css';

/**
 * Who may use the app: everybody (a grant to the built-in group), administrators only (no grant), or selected groups
 * and users, through the grants of the permission model (step 11). Applied at once and optimistically.
 */
export function AccessSection({ app, appId }: { app: PithyxApp; appId: string }) {
  const { t } = useTranslation('store');
  const ids = useId();
  const queryClient = useQueryClient();
  const key = ['store', 'grants', appId] as const;
  const grants = useQuery({
    queryKey: key,
    queryFn: () => storeCall(app, 'internal.admin.apps.grants', { appId }),
  });
  const groups = useQuery({
    queryKey: ['groups'],
    queryFn: () => usersCall(app, 'internal.admin.groups.list', {}),
  });
  // "Selected" with nothing selected yet is a choice of this screen; the daemon only knows the grants.
  const [selecting, setSelecting] = useState(false);
  const change = useMutation({
    mutationFn: (next: SubjectGrant[]) =>
      storeCall(app, 'internal.admin.apps.setGrants', { appId, grants: next }),
    onMutate: async (next) => {
      await queryClient.cancelQueries({ queryKey: key });
      const previous = queryClient.getQueryData<AppGrants>(key);
      if (previous !== undefined) {
        queryClient.setQueryData(key, { ...previous, grants: next });
      }
      return { previous };
    },
    onError: (_error, _next, context) => {
      if (context?.previous !== undefined) {
        queryClient.setQueryData(key, context.previous);
      }
    },
    onSuccess: (answer) => {
      queryClient.setQueryData(key, answer);
    },
  });
  const everybody = groups.data?.find((group) => group.builtin);
  const current = grants.data?.grants ?? [];
  const mode = everybody === undefined ? 'admins' : accessOf(current, everybody.id);
  const shown = selecting && mode === 'admins' ? 'some' : mode;
  const choose = (choice: AppAccess | 'some') => {
    setSelecting(choice === 'some');
    if (choice === 'everybody' && everybody !== undefined) {
      change.mutate([{ subjectKind: 'group', subjectId: everybody.id, level: 'allow' }]);
    } else if (choice === 'admins') {
      change.mutate([]);
    } else if (mode === 'everybody') {
      change.mutate([]);
    }
  };
  const has = (kind: SubjectGrant['subjectKind'], id: number) =>
    current.some((grant) => grant.subjectKind === kind && grant.subjectId === id);

  return (
    <section className={styles.group} aria-labelledby="store-access">
      <h2 id="store-access" className={styles.sectionTitle}>
        {t('detail.access.title')}
      </h2>
      <p className={styles.muted}>{t('detail.access.intro')}</p>
      {(grants.isPending || groups.isPending) && (
        <p className={styles.loading} role="status">
          {t('detail.access.loading')}
        </p>
      )}
      {grants.isError && <ErrorNotice error={describeProblem(grants.error)} />}
      {groups.isError && <ErrorNotice error={describeProblem(groups.error)} />}
      {grants.data !== undefined && groups.data !== undefined && (
        <>
          <fieldset className={styles.choices}>
            <legend className="visually-hidden">{t('detail.access.label')}</legend>
            {(['everybody', 'admins', 'some'] as const).map((choice) => (
              <label key={choice} className={styles.check}>
                <input
                  type="radio"
                  name={`${ids}-access`}
                  checked={shown === choice}
                  onChange={() => {
                    choose(choice);
                  }}
                />
                <span>{t(`detail.access.${choice}`)}</span>
              </label>
            ))}
          </fieldset>
          {shown === 'some' && (
            <>
              <h3 className={styles.subTitle}>{t('detail.access.groups')}</h3>
              <ul className={styles.subjects}>
                {groups.data
                  .filter((group) => !group.builtin)
                  .map((group) => (
                    <li key={`group-${String(group.id)}`}>
                      <Checkbox
                        isSelected={has('group', group.id)}
                        onChange={(allowed) => {
                          change.mutate(withSubject(current, 'group', group.id, allowed));
                        }}
                      >
                        {group.name}
                      </Checkbox>
                    </li>
                  ))}
              </ul>
              <h3 className={styles.subTitle}>{t('detail.access.users')}</h3>
              <ul className={styles.subjects}>
                {grants.data.users.map((user) => {
                  const label = user.displayName ?? user.username;
                  const own = has('user', user.userId);
                  return (
                    <li key={`user-${String(user.userId)}`} className={styles.subject}>
                      {user.administrator ? (
                        <span>
                          {label}{' '}
                          <span className={styles.muted}>({t('detail.access.administrator')})</span>
                        </span>
                      ) : (
                        <Checkbox
                          isSelected={own}
                          onChange={(allowed) => {
                            change.mutate(withSubject(current, 'user', user.userId, allowed));
                          }}
                        >
                          {label}
                        </Checkbox>
                      )}
                      {user.disabled && (
                        <span className={styles.muted}>{t('detail.access.deactivated')}</span>
                      )}
                      {!user.administrator && !own && user.allowed && (
                        <span className={styles.muted}>{t('detail.access.viaGroup')}</span>
                      )}
                    </li>
                  );
                })}
              </ul>
            </>
          )}
        </>
      )}
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
    </section>
  );
}
