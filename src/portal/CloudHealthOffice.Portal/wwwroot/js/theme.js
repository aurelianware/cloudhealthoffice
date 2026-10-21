// Portal theme switch. The server renders <html data-cho-theme> from the cho-theme
// cookie on first load; this flips it live and remembers the choice for a year.
window.choTheme = {
  set: function (name) {
    document.documentElement.setAttribute('data-cho-theme', name);
    var secure = location.protocol === 'https:' ? '; Secure' : '';
    document.cookie = 'cho-theme=' + encodeURIComponent(name) + '; path=/; max-age=31536000; SameSite=Lax' + secure;
  }
};
