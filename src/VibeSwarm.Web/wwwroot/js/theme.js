(function () {
	const cookieName = 'VibeSwarm.ThemePreference';
	const themeColorMap = {
		light: '#f8f9fa',
		dark: '#1a1d21'
	};
	const mediaQuery = window.matchMedia
		? window.matchMedia('(prefers-color-scheme: dark)')
		: null;

	function normalizeTheme(value) {
		const normalized = String(value || 'system').trim().toLowerCase();
		return normalized === 'light' || normalized === 'dark' || normalized === 'system'
			? normalized
			: 'system';
	}

	function getSystemTheme() {
		return mediaQuery && mediaQuery.matches ? 'dark' : 'light';
	}

	function getEffectiveTheme(preference) {
		const normalized = normalizeTheme(preference);
		return normalized === 'system' ? getSystemTheme() : normalized;
	}

	function updateThemeColor() {
		const appliedTheme = document.documentElement.getAttribute('data-bs-theme') || 'light';
		const themeColor = themeColorMap[appliedTheme] || themeColorMap.light;
		const metaTag = document.querySelector('meta[name="theme-color"]');

		if (metaTag) {
			metaTag.setAttribute('content', themeColor);
		}
	}

	function applyTheme(preference) {
		const normalized = normalizeTheme(preference);
		const effective = getEffectiveTheme(normalized);
		document.documentElement.setAttribute('data-vs-theme', normalized);
		document.documentElement.setAttribute('data-bs-theme', effective);
		document.documentElement.setAttribute('data-theme', effective);
		updateThemeColor();
		return normalized;
	}

	function readCookieTheme() {
		const cookies = document.cookie ? document.cookie.split(';') : [];

		for (let index = 0; index < cookies.length; index += 1) {
			const cookie = cookies[index].trim();
			if (!cookie.startsWith(cookieName + '=')) {
				continue;
			}

			return normalizeTheme(decodeURIComponent(cookie.substring(cookieName.length + 1)));
		}

		return null;
	}

	function writeCookieTheme(preference) {
		document.cookie = cookieName + '=' + encodeURIComponent(normalizeTheme(preference))
			+ '; path=/; max-age=31536000; samesite=lax';
	}

	function clearCookieTheme() {
		document.cookie = cookieName + '=; path=/; max-age=0; samesite=lax';
	}

	function handleSystemThemeChanged() {
		if (normalizeTheme(document.documentElement.getAttribute('data-vs-theme')) === 'system') {
			applyTheme('system');
		}
	}

	if (mediaQuery) {
		if (typeof mediaQuery.addEventListener === 'function') {
			mediaQuery.addEventListener('change', handleSystemThemeChanged);
		}
		else if (typeof mediaQuery.addListener === 'function') {
			mediaQuery.addListener(handleSystemThemeChanged);
		}
	}

	window.vibeSwarmTheme = {
		bootstrap: function () {
			applyTheme(readCookieTheme() || 'system');
		},
		getPreference: function () {
			return normalizeTheme(document.documentElement.getAttribute('data-vs-theme'));
		},
		getAppliedTheme: function () {
			return document.documentElement.getAttribute('data-bs-theme') || getEffectiveTheme('system');
		},
		setPreference: function (preference, persistCookie) {
			const normalized = applyTheme(preference);
			if (persistCookie !== false) {
				writeCookieTheme(normalized);
			}

			return normalized;
		},
		clearPreference: function () {
			clearCookieTheme();
			return applyTheme('system');
		}
	};
})();
